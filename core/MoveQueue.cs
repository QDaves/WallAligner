using Qx;
using Qx.Model;
using System.Diagnostics;

namespace WallAligner.Core;

public sealed record WallMove(WallEntry before, WallLocation after);

public sealed class MoveQueue : IDisposable
{
    readonly IWallSession session;
    readonly object gate = new();
    readonly Stack<WallMove[]> undo_stack = new();
    readonly Stack<WallMove[]> redo_stack = new();
    readonly SemaphoreSlim send_gate = new(1);
    long last_send;
    CancellationTokenSource? current;
    CancellationTokenSource? batch;
    TaskCompletionSource? pause_waiter;
    Dictionary<Id, WallMove> targets = [];
    bool live_edit;
    long generation = -1;
    public bool busy { get { lock (gate) return current is not null; } }
    public bool can_revise { get { lock (gate) return current is not null && live_edit; } }
    public bool paused { get { lock (gate) return pause_waiter is not null; } }
    public bool can_undo { get { lock (gate) return current is null && undo_stack.Count > 0; } }
    public bool can_redo { get { lock (gate) return current is null && redo_stack.Count > 0; } }
    public event Action<string, int, int>? progress;
    public event Action<IReadOnlyList<WallMove>>? started;
    public event Action<WallMove>? item_confirmed;

    public MoveQueue(IWallSession session)
    {
        this.session = session;
        session.changed += room_changed;
    }

    void room_changed()
    {
        lock (gate)
        {
            long next = session.room?.generation ?? -1;
            if (next == generation) return;
            generation = next;
            current?.Cancel();
            undo_stack.Clear();
            redo_stack.Clear();
        }
    }

    public void cancel() { lock (gate) current?.Cancel(); }

    public bool pause()
    {
        lock (gate)
        {
            if (current is null || !live_edit) return false;
            pause_waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            batch?.Cancel();
            return true;
        }
    }

    public void resume(IReadOnlyList<WallMove>? changes = null)
    {
        lock (gate)
        {
            if (pause_waiter is not { } waiter) return;
            if (changes is not null)
                foreach (var move in changes)
                    targets[move.before.id] = targets.TryGetValue(move.before.id, out var previous)
                        ? previous with { after = move.after } : move;
            pause_waiter = null;
            waiter.TrySetResult();
        }
    }

    public async Task apply(IReadOnlyList<WallMove> moves)
    {
        if (busy) return;
        var finished = await run(moves, true);
        lock (gate)
        {
            if (finished.Length == 0) return;
            undo_stack.Push(finished);
            if (undo_stack.Count > 30)
            {
                var keep = undo_stack.Take(30).Reverse().ToArray();
                undo_stack.Clear();
                foreach (var item in keep) undo_stack.Push(item);
            }
            redo_stack.Clear();
        }
    }

    public async Task undo()
    {
        WallMove[] original;
        lock (gate)
        {
            if (!can_undo) return;
            original = undo_stack.Peek();
        }
        var inverse = original.Select(m => new WallMove(m.before with { location = m.after }, m.before.location)).ToArray();
        var restored = await run(inverse);
        lock (gate)
        {
            if (restored.Length == 0 || undo_stack.Count == 0 || !ReferenceEquals(undo_stack.Peek(), original)) return;
            undo_stack.Pop();
            var ids = restored.Select(x => x.before.id).ToHashSet();
            redo_stack.Push(original.Where(m => ids.Contains(m.before.id)).ToArray());
            var remaining = original.Where(m => !ids.Contains(m.before.id)).ToArray();
            if (remaining.Length > 0) undo_stack.Push(remaining);
        }
    }

    public async Task redo()
    {
        WallMove[] original;
        lock (gate)
        {
            if (!can_redo) return;
            original = redo_stack.Peek();
        }
        var restored = await run(original);
        lock (gate)
        {
            if (restored.Length == 0 || redo_stack.Count == 0 || !ReferenceEquals(redo_stack.Peek(), original)) return;
            redo_stack.Pop();
            undo_stack.Push(restored);
            var ids = restored.Select(x => x.before.id).ToHashSet();
            var remaining = original.Where(m => !ids.Contains(m.before.id)).ToArray();
            if (remaining.Length > 0) redo_stack.Push(remaining);
        }
    }

    async Task<WallMove[]> run(IReadOnlyList<WallMove> moves, bool editable = false)
    {
        var room = session.room ?? throw new InvalidOperationException("Enter a room first.");
        if (!room.can_edit) throw new InvalidOperationException("You need building rights in this room.");
        using var cancellation = new CancellationTokenSource();
        lock (gate)
        {
            if (current is not null) return [];
            room_changed();
            current = cancellation;
            live_edit = editable;
            targets = moves.Where(m => m.before.location != m.after).DistinctBy(m => m.before.id)
                .ToDictionary(m => m.before.id);
        }
        var token = cancellation.Token;
        var accepted = new Dictionary<Id, WallMove>();
        var expected = new Dictionary<Id, WallLocation>();
        WallMove[] pending = [];
        int completed = 0;
        int failed = 0;
        string? failure = null;
        string? final_status = null;
        try
        {
            while (true)
            {
                Task? waiting;
                lock (gate) waiting = pause_waiter?.Task;
                if (waiting is not null) await waiting.WaitAsync(token);
                token.ThrowIfCancellationRequested();
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(token);
                lock (gate)
                {
                    if (pause_waiter is not null) continue;
                    batch = cycle;
                    pending = targets.Values.ToArray();
                }
                foreach (var move in pending) expected.TryAdd(move.before.id, move.before.location);
                completed = failed = 0;
                failure = null;
                started?.Invoke(pending);
                progress?.Invoke("", 0, pending.Length);
                try
                {
                    await Parallel.ForEachAsync(pending, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 4, CancellationToken = cycle.Token
                    }, async (move, dispatch_token) =>
                    {
                        bool confirmed = false;
                        for (int attempt = 0; attempt < 4; attempt++)
                        {
                            dispatch_token.ThrowIfCancellationRequested();
                            var now = session.room;
                            if (now is null || now.generation != room.generation)
                                throw new OperationCanceledException();
                            var found = now.items.FirstOrDefault(x => x.id == move.before.id);
                            if (found?.location == move.after) { confirmed = true; break; }
                            WallLocation previous;
                            lock (accepted) previous = expected[move.before.id];
                            if (found is null || found.kind != move.before.kind || found.location != previous) break;
                            try
                            {
                                if (await send(new WallMove(found, move.after), room.generation, token, dispatch_token))
                                { confirmed = true; break; }
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception error)
                            {
                                if (session.room is { } latest && latest.generation == room.generation &&
                                    latest.items.Any(x => x.id == move.before.id && x.location == move.after))
                                { confirmed = true; break; }
                                Interlocked.CompareExchange(ref failure, error.Message, null);
                                break;
                            }
                        }
                        lock (accepted)
                        {
                            if (confirmed)
                            {
                                accepted[move.before.id] = move;
                                expected[move.before.id] = move.after;
                                item_confirmed?.Invoke(move);
                            }
                            else failed++;
                            completed++;
                            progress?.Invoke("", completed, pending.Length);
                        }
                    });
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && cycle.IsCancellationRequested)
                {
                }
                finally
                {
                    lock (gate) if (ReferenceEquals(batch, cycle)) batch = null;
                }
                lock (gate)
                {
                    if (pause_waiter is not null || cycle.IsCancellationRequested) continue;
                    live_edit = false;
                    break;
                }
            }
            final_status = failed == 0 ? "" : $"{failed} moves were not confirmed" + (failure is null ? "" : $": {failure}");
        }
        catch (OperationCanceledException)
        {
            if (session.room?.generation == room.generation)
            {
                foreach (var move in pending)
                    if (session.room.items.Any(x => x.id == move.before.id && x.location == move.after))
                        accepted[move.before.id] = move;
            }
            else accepted.Clear();
            final_status = "";
        }
        finally
        {
            lock (gate)
            {
                current = null;
                batch = null;
                pause_waiter = null;
                live_edit = false;
                targets.Clear();
                if (session.room?.generation != room.generation) accepted.Clear();
            }
            progress?.Invoke(final_status ?? "", completed, pending.Length);
        }
        return accepted.Values.Where(move => move.before.location != move.after).ToArray();
    }

    async Task<bool> send(WallMove move, long room_generation, CancellationToken token, CancellationToken dispatch_token)
    {
        Task<bool> confirmation;
        await send_gate.WaitAsync(dispatch_token).ConfigureAwait(false);
        try
        {
            if (last_send != 0)
            {
                var remaining = TimeSpan.FromMilliseconds(66) - Stopwatch.GetElapsedTime(last_send);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, dispatch_token).ConfigureAwait(false);
            }
            var room = session.room;
            if (room is null || room.generation != room_generation) throw new OperationCanceledException(token);
            var item = room.items.FirstOrDefault(x => x.id == move.before.id);
            if (item?.location == move.after) return true;
            if (item is null || item.kind != move.before.kind || item.location != move.before.location) return false;
            lock (gate)
            {
                dispatch_token.ThrowIfCancellationRequested();
                confirmation = session.move(move.before, move.after, room_generation, token);
                last_send = Stopwatch.GetTimestamp();
            }
        }
        finally { send_gate.Release(); }
        return await confirmation.ConfigureAwait(false);
    }

    public void Dispose()
    {
        session.changed -= room_changed;
        cancel();
    }
}
