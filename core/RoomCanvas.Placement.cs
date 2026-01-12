using Avalonia.Threading;
using Xabbo.Core;
using Xabbo.Messages.Flash;

namespace WallAligner.Core;

public partial class RoomCanvas
{
    public async void applysvg(string filepath, string pathdata, string furniname)
    {
        var room = currentroom;
        if (room == null || extension == null) return;

        bool hasfile = !string.IsNullOrWhiteSpace(filepath);
        bool haspath = !string.IsNullOrWhiteSpace(pathdata);

        if (!hasfile && !haspath)
        {
            statuschanged?.Invoke("missing file or path data");
            return;
        }

        if (string.IsNullOrWhiteSpace(furniname))
        {
            statuschanged?.Invoke("missing furniture name");
            return;
        }

        if (hasfile && !File.Exists(filepath))
        {
            statuschanged?.Invoke($"file not found: {filepath}");
            return;
        }

        if (previewlocations == null || previewlocations.Count == 0)
        {
            statuschanged?.Invoke("no preview available");
            return;
        }

        try
        {
            var matchingitems = room.WallItems
                .Where(x => x.GetName()?.Equals(furniname, StringComparison.OrdinalIgnoreCase) == true)
                .OrderBy(x => x.Id)
                .ToArray();

            if (matchingitems.Length == 0)
            {
                statuschanged?.Invoke($"no items found with name '{furniname}'");
                return;
            }

            selected.Clear();
            foreach (var item in matchingitems)
                selected.Add(item.Id);

            var locations = previewlocations.ToList();
            clearpreview();

            var originallocs = new Dictionary<long, WallLocation>();
            for (int i = 0; i < matchingitems.Length; i++)
            {
                originallocs[matchingitems[i].Id] = matchingitems[i].Location;
            }

            applypathlocations(matchingitems, locations);

            _ = Task.Run(async () =>
            {
                for (int round = 1; round <= 3; round++)
                {
                    await Task.Delay(2000);

                    var faileditems = new List<(IWallItem item, WallLocation targetloc)>();

                    for (int i = 0; i < matchingitems.Length; i++)
                    {
                        var item = matchingitems[i];
                        var targetloc = locations[i];
                        var currentloc = item.Location;
                        var originalloc = originallocs[item.Id];

                        if (currentloc.ToString() == originalloc.ToString())
                        {
                            faileditems.Add((item, targetloc));
                        }
                    }

                    if (faileditems.Count == 0) break;

                    foreach (var (item, targetloc) in faileditems)
                    {
                        try
                        {
                            extension.Send(Out.MoveWallItem, item.Id, targetloc.ToString());
                            await Task.Delay(66);
                        }
                        catch { }
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    statuschanged?.Invoke($"applied svg to {matchingitems.Length} items");
                });
            });

            InvalidateVisual();
        }
        catch (Exception ex)
        {
            statuschanged?.Invoke($"error: {ex.Message}");
        }
    }

    private void applypathlocations(IWallItem[] items, List<WallLocation> locations)
    {
        if (items.Length != locations.Count) return;
        if (extension == null) return;

        lock (placementlock)
        {
            placementqueue.Enqueue((items, locations));

            if (!isplacing)
            {
                isplacing = true;
                processplacementqueue();
            }
        }
    }

    private void processplacementqueue()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                (IWallItem[] items, List<WallLocation> locations) job;

                lock (placementlock)
                {
                    if (placementqueue.Count == 0)
                    {
                        isplacing = false;
                        return;
                    }
                    job = placementqueue.Dequeue();
                }

                int totalinqueue;
                lock (placementlock)
                {
                    totalinqueue = placementqueue.Count;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    var queuetext = totalinqueue > 0 ? $" ({totalinqueue} queued)" : "";
                    statuschanged?.Invoke($"placing {job.items.Length} items...{queuetext}");
                });

                for (int i = 0; i < job.items.Length; i++)
                {
                    var item = job.items[i];
                    var loc = job.locations[i];
                    var locstr = loc.ToString();

                    try
                    {
                        extension?.Send(Out.MoveWallItem, item.Id, locstr);
                    }
                    catch { }

                    await Task.Delay(66);
                }

                Dispatcher.UIThread.Post(() =>
                {
                    int remaining;
                    lock (placementlock)
                    {
                        remaining = placementqueue.Count;
                    }
                    if (remaining == 0)
                        statuschanged?.Invoke($"placed {job.items.Length} items");
                    else
                        statuschanged?.Invoke($"placed {job.items.Length} items, {remaining} jobs remaining");
                });
            }
        });
    }
}
