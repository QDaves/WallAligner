using System.ComponentModel;
using Avalonia;
using Avalonia.Media;

namespace WallAligner.Core;

public sealed class WallDrawing(int number, string tool) : INotifyPropertyChanged
{
    public int number { get; } = number;
    public string tool { get; } = tool;
    public string text = "Hello";
    public string font = "Segoe UI";
    public string svg = "";
    public string shape = "Freehand";
    public int kind;
    public int requested_count = 50;
    public bool use_all;
    public double width = 300;
    public double rotation;
    public Point origin;
    public IReadOnlyList<Point[]> contours = [];
    public Point[] points = [];
    public Point[][] outline = [];
    public StreamGeometry? geometry;
    public Rect bounds;
    public WallEntry[] items = [];
    public string? error;
    public string? placement_error;
    public string? assignment_note;
    public string label => tool == "text" ? $"{number}. {text.Replace('\n', ' ').Trim()}" :
        $"{number}. {(tool == "svg" ? "SVG" : shape)}";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void refresh_label() => PropertyChanged?.Invoke(this, new(nameof(label)));

    public void refresh_geometry()
    {
        var points = outline.SelectMany(x => x).ToArray();
        bounds = points.Length == 0 ? default : new Rect(new Point(points.Min(x => x.X), points.Min(x => x.Y)),
            new Point(points.Max(x => x.X), points.Max(x => x.Y)));
        geometry = new StreamGeometry();
        using var path = geometry.Open();
        foreach (var contour in outline.Where(x => x.Length > 1))
        {
            path.BeginFigure(contour[0], false);
            foreach (var point in contour.Skip(1)) path.LineTo(point);
            path.EndFigure(false);
        }
    }

    public static void layout(IReadOnlyList<WallDrawing> drawings, RoomView room, WallDrawing? preferred = null)
    {
        var previous = drawings.ToDictionary(x => x, x => x.items);
        var owners = new Dictionary<Qx.Id, WallDrawing>();
        foreach (var drawing in drawings)
            foreach (var item in drawing.items) owners.TryAdd(item.id, drawing);
        foreach (var drawing in drawings)
        {
            drawing.placement_error = null;
            drawing.items = [];
            drawing.points = [];
            drawing.outline = PatternBuilder.outline(drawing.contours, drawing.width, drawing.rotation);
            drawing.refresh_geometry();
        }
        foreach (var group in drawings.Where(x => x.contours.Count > 0 && x.error is null).GroupBy(x => x.kind))
        {
            var objects = group.ToArray();
            var available = room.items.Where(x => x.kind == group.Key).OrderBy(x => x.id).ToArray();
            if (available.Length < objects.Length)
            {
                foreach (var drawing in objects) drawing.placement_error = $"{objects.Length} objects need at least {objects.Length} items of this type; {available.Length} available.";
            }
            var counts = objects.Select(x => Math.Max(1, x.requested_count)).ToArray();
            var unlimited = Enumerable.Range(0, objects.Length).Where(i => objects[i].use_all).ToArray();
            if (unlimited.Length > 0)
            {
                int remaining = Math.Max(unlimited.Length, available.Length -
                    Enumerable.Range(0, objects.Length).Where(i => !objects[i].use_all).Sum(i => counts[i]));
                var weights = unlimited.Select(i => Math.Max(1, objects[i].outline.Sum(path =>
                    Enumerable.Range(1, path.Length - 1).Sum(p => ((Vector)(path[p] - path[p - 1])).Length)))).ToArray();
                double total = weights.Sum();
                var quotas = weights.Select(length => 1 + (remaining - unlimited.Length) * length / total).ToArray();
                for (int i = 0; i < unlimited.Length; i++) counts[unlimited[i]] = (int)Math.Floor(quotas[i]);
                int left = remaining - unlimited.Sum(i => counts[i]);
                foreach (int index in Enumerable.Range(0, unlimited.Length).OrderByDescending(i => quotas[i] - Math.Floor(quotas[i])).Take(left))
                    counts[unlimited[index]]++;
            }
            if (counts.Sum(x => (long)x) > available.Length)
            {
                int minimum = available.Length >= objects.Length ? 1 : 0;
                int priority = preferred is not null && !preferred.use_all ? Array.IndexOf(objects, preferred) : -1;
                int allocated = priority >= 0 ? Math.Min(counts[priority], available.Length - (objects.Length - 1) * minimum) : 0;
                var indices = Enumerable.Range(0, objects.Length).Where(i => i != priority).ToArray();
                int remaining = available.Length - allocated - indices.Length * minimum;
                double total = indices.Sum(i => (double)(counts[i] - minimum));
                var quotas = indices.Select(i => total == 0 ? 0 : remaining * (counts[i] - minimum) / total).ToArray();
                for (int i = 0; i < indices.Length; i++) counts[indices[i]] = minimum + (int)Math.Floor(quotas[i]);
                if (priority >= 0) counts[priority] = allocated;
                int left = available.Length - counts.Sum();
                foreach (int index in Enumerable.Range(0, indices.Length).OrderByDescending(i => quotas[i] - Math.Floor(quotas[i])).Take(left))
                    counts[indices[index]]++;
            }
            var current = available.ToDictionary(x => x.id);
            var reserved = new HashSet<Qx.Id>();
            var assignments = objects.Select((drawing, i) => previous[drawing]
                .Where(item => current.ContainsKey(item.id) && reserved.Add(item.id))
                .Take(counts[i]).Select(item => current[item.id]).ToList()).ToArray();
            reserved = assignments.SelectMany(x => x).Select(x => x.id).ToHashSet();
            var free = new Queue<WallEntry>(available.Where(x => !reserved.Contains(x.id)));
            for (int i = 0; i < objects.Length; i++)
            {
                var drawing = objects[i];
                while (assignments[i].Count < counts[i] && free.TryDequeue(out var item)) assignments[i].Add(item);
                drawing.points = counts[i] > 0 ? PatternBuilder.sample(drawing.contours, counts[i], drawing.width, drawing.rotation) : [];
                drawing.items = assignments[i].ToArray();
                if (!drawing.items.Select(x => x.id).SequenceEqual(previous[drawing].Select(x => x.id)))
                {
                    var assigned = drawing.items.Select(x => x.id).ToHashSet();
                    int borrowed = drawing.items.Count(item => owners.TryGetValue(item.id, out var owner) && owner != drawing);
                    int removed = previous[drawing].Count(item => !assigned.Contains(item.id));
                    drawing.assignment_note = borrowed > 0 ? $"{borrowed} {(borrowed == 1 ? "item" : "items")} reassigned from other objects."
                        : removed > 0 && preferred != drawing ? $"{removed} {(removed == 1 ? "item" : "items")} released to other objects." : null;
                }
            }
        }
    }
}
