using Qx;
using Qx.Game.Api;
using Qx.Game.Rooms;
using Qx.Hosting;
using Qx.Interception.GEarth;
using Qx.Model;
using Qx.Protocol;
using Qx.Model.Messages.Incoming;
using Qx.Game.Messages;

namespace WallAligner.Core;

public sealed record WallEntry(Id id, int kind, string name, WallLocation location);

public sealed record RoomView(long id, long generation, string name, FloorPlan plan,
    Point entry, bool can_edit, IReadOnlyList<WallEntry> items);

public interface IWallSession : IAsyncDisposable
{
    RoomView? room { get; }
    string status { get; }
    event Action? changed;
    event Action<WallEntry>? item_changed;
    event Action<WallLocation>? start_picked;
    bool pick_start { get; set; }
    void preview(IReadOnlyList<WallMove> moves);
    void restore_preview();
    Task<bool> move(WallEntry item, WallLocation target, long generation, CancellationToken token);
}

public sealed class WallSession : IWallSession
{
    public const int preview_limit = 350;
    readonly GameHost host;
    readonly object snapshot_gate = new();
    readonly Dictionary<Id, int> item_indices = [];
    readonly Dictionary<Id, WallItem> furniture = [];
    readonly Dictionary<Id, (WallLocation target, WallEntry source)> previews = [];
    long preview_generation = -1;
    readonly IDisposable move_capture;
    public event Action? changed;
    public event Action<WallEntry>? item_changed;
    public event Action<WallLocation>? start_picked;
    public bool pick_start { get; set; }
    public event Action? activated;
    public RoomView? room { get; private set; }
    public string status { get; private set; } = "Connecting to G-Earth";

    public WallSession(string[] args)
    {
        var options = GEarthOptions.Parse(args, new GEarthOptions
        {
            Title = "WallAligner",
            Description = "Arrange wall furniture, draw shapes and align directly over the room.",
            Author = "QDave",
            Version = "2.0.0",
            OnClickUsed = true
        });
        host = new GameHost(new GameHostOptions { GEarth = options, ReconnectTransport = true });
        host.Extension.Activated += () => activated?.Invoke();
        host.Extension.Connected += _ => refresh();
        host.Extension.Disconnected += refresh;
        host.Game.GameData.Loaded += refresh;
        var source = host.Game.Room;
        source.Ready += refresh;
        source.Left += refresh;
        source.WallItemsLoaded += refresh;
        source.WallItemAdded += item => { refresh(); if (room?.items.FirstOrDefault(x => x.id == item.Id) is { } entry) item_changed?.Invoke(entry); };
        source.WallItemUpdated += update;
        source.WallItemRemoved += _ => refresh();
        source.EntryTileUpdated += _ => refresh();
        source.RightsLevelChanged += (_, _) => refresh();
        source.RoomDataUpdated += _ => refresh();
        move_capture = host.Extension.Intercept(MessageContracts.Room.WallItem.Move.Key, intercept =>
        {
            if (!pick_start) return;
            int position = 0;
            var reader = intercept.Packet.ReaderAt(ref position);
            var request = MessageContracts.Room.WallItem.Move.Parse(in reader);
            intercept.Block();
            pick_start = false;
            display(request.ItemId, room?.items.FirstOrDefault(x => x.id == request.ItemId)?.location);
            start_picked?.Invoke(request.Location);
        });
    }

    public async Task start(CancellationToken token)
    {
        await host.StartAsync(token).ConfigureAwait(false);
        refresh();
        await host.TransportTask.WaitAsync(token).ConfigureAwait(false);
    }

    void refresh()
    {
        lock (snapshot_gate)
        {
            item_indices.Clear();
            furniture.Clear();
            var source = host.Game.Room;
            if (source.IsInRoom && source.FloorPlan is { } plan && source.WallItemsAreLoaded)
            {
                var items = source.WallItems.Select(item =>
                {
                    var info = host.Game.GameData.Furni?.GetInfo(item);
                    string name = info?.Name ?? $"Wall item {item.Kind}";
                    if (info?.ClassName == "poster" &&
                        host.Game.GameData.Texts?[$"poster_{item.Data}_name"] is { Length: > 0 } poster)
                        name = poster;
                    return new WallEntry(item.Id, item.Kind, name, item.Location);
                }).ToArray();
                for (int i = 0; i < items.Length; i++) item_indices[items[i].id] = i;
                foreach (var item in source.WallItems) furniture[item.Id] = item;
                room = new(source.RoomId, source.Generation, source.Data?.Name ?? "Room", plan,
                    source.EntryTile is { } tile ? new Point(tile.X, tile.Y) : new Point(-1, -1),
                    source.HasRights, items);
                status = source.HasRights ? "Connected" : "Room is read-only";
            }
            else
            {
                room = null;
                status = host.Extension.IsInterceptorConnected ? "Enter or reload a room" : "Waiting for G-Earth";
            }
        }
        changed?.Invoke();
    }

    void update(WallItem item)
    {
        lock (snapshot_gate)
        {
            furniture[item.Id] = item;
            if (room is not { } current || !item_indices.TryGetValue(item.Id, out int index)) return;
            var items = current.items.ToArray();
            items[index] = items[index] with { location = item.Location };
            room = current with { items = items };
        }
        changed?.Invoke();
        if (room?.items.FirstOrDefault(x => x.id == item.Id) is { } updated) item_changed?.Invoke(updated);
    }

    public void preview(IReadOnlyList<WallMove> moves)
    {
        if (room is not { can_edit: true } current) return;
        if (moves.Count > preview_limit) { restore_preview(); return; }
        if (preview_generation != current.generation)
        {
            previews.Clear();
            preview_generation = current.generation;
        }
        var visible = moves;
        var sources = current.items.ToDictionary(item => item.id);
        var ids = visible.Select(move => move.before.id).ToHashSet();
        foreach (var id in previews.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            display(id, current.items.FirstOrDefault(x => x.id == id)?.location);
            previews.Remove(id);
        }
        foreach (var move in visible)
        {
            if (!sources.TryGetValue(move.before.id, out var source)) continue;
            if (previews.TryGetValue(move.before.id, out var shown) && shown.target == move.after && ReferenceEquals(shown.source, source)) continue;
            display(move.before.id, move.after);
            previews[move.before.id] = (move.after, source);
        }
    }

    public void restore_preview()
    {
        if (room is { } current && current.generation == preview_generation)
            foreach (var id in previews.Keys)
                display(id, current.items.FirstOrDefault(x => x.id == id)?.location);
        previews.Clear();
    }

    void display(Id id, WallLocation? location)
    {
        if (location is null) return;
        WallItem? source;
        lock (snapshot_gate) furniture.TryGetValue(id, out source);
        if (source is null) return;
        if (!host.Messages.TryGetHeader(MessageKeys.Room.WallItem.Updated, out var header))
            throw new InvalidOperationException("The client wall-item update is not available yet. Reload the room.");
        var item = new WallItem
        {
            Id = source.Id, Kind = source.Kind, Location = location.Value, Data = source.Data,
            OwnerId = source.OwnerId, OwnerName = source.OwnerName, Usage = source.Usage,
            SecondsToExpiration = source.SecondsToExpiration, Identifier = source.Identifier
        };
        var catalog = host.Extension.CaptureSessionCatalog();
        using var packet = host.Messages.CreatePacket(header);
        new WallItemUpdate(item).Compose(packet.Writer());
        host.Extension.Send(packet, catalog.Session, catalog.Catalog);
    }

    public async Task<bool> move(WallEntry item, WallLocation target, long generation, CancellationToken token)
    {
        if (item.location == target) return true;
        var confirmed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void check()
        {
            var current = room;
            if (current is null || current.generation != generation)
                confirmed.TrySetCanceled();
            else if (current.items.FirstOrDefault(x => x.id == item.id) is not { } found)
                confirmed.TrySetResult(false);
            else if (found.location == target)
                confirmed.TrySetResult(true);
        }
        changed += check;
        try
        {
            var position = new RoomPlacementWallPosition(target.Wall.X, target.Wall.Y,
                target.Offset.X, target.Offset.Y, target.Orientation.ToString());
            var original = new RoomPlacementWallPosition(item.location.Wall.X, item.location.Wall.Y,
                item.location.Offset.X, item.location.Offset.Y, item.location.Orientation.ToString());
            await host.Api.InvokeAsync<RoomPlacementWallMoveRequest, RoomPlacementDispatchReceipt>(
                ApiIds.RoomPlacementWallMove, new(item.id, position, original, ExpectedRoomGeneration: generation), token);
            check();
            return await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        }
        catch (TimeoutException) { return false; }
        finally { changed -= check; }
    }

    public async ValueTask DisposeAsync()
    {
        move_capture.Dispose();
        try { restore_preview(); }
        finally { await host.DisposeAsync().ConfigureAwait(false); }
    }
}
