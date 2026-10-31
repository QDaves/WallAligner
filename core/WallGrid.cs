using System.Globalization;
using System.Text;
using Qx;
using Qx.Model;
using Point = Avalonia.Point;

namespace WallAligner.Core;

public sealed record GridCell(Id id, int column, int row, WallLocation? saved = null)
{
    public override string ToString() => $"{id}   [{column}, {row}]";
}

public sealed record GridGroup(string name, Id[] ids)
{
    public override string ToString() => name;
}

public sealed class WallGrid
{
    public int columns = 5;
    public int rows = 5;
    public int spacing_x = 8;
    public int spacing_y = 8;
    public int slope;
    public int layer;
    public WallLocation? start;
    public List<GridCell> cells = [];
    public bool custom_cells;

    public void assign(IEnumerable<Id> ids, RoomView room)
    {
        if (columns is < 1 or > 20000) throw new ArgumentException("Choose a valid column count first.");
        var unique = ids.Distinct().ToArray();
        if (unique.Length > 20000) throw new ArgumentException("A grid supports up to 20,000 items.");
        cells = unique.Select((id, i) => new GridCell(id, i % columns, i / columns)).ToList();
        rows = Math.Max(1, (cells.Count + columns - 1) / columns);
        custom_cells = false;
        start ??= room.items.FirstOrDefault(x => unique.Contains(x.id))?.location;
    }

    public IReadOnlyList<WallMove> moves(RoomView room)
    {
        validate();
        if (start is not { } anchor || cells.Count == 0) return [];
        var available = room.items.ToDictionary(x => x.id);
        var missing = cells.Where(x => !available.ContainsKey(x.id)).Select(x => x.id).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException($"{missing.Length} grid items are missing from this room.");
        double unit = 64.0 / room.plan.Scale;
        var origin = WallLayout.project(room, anchor);
        var result = new List<WallMove>();
        for (int i = 0; i < cells.Count; i++)
        {
            int column = custom_cells ? cells[i].column : i % columns;
            int row = custom_cells ? cells[i].row : i / columns;
            if (column >= columns || row >= rows) continue;
            var target = origin + new Avalonia.Vector(column * spacing_x * unit, (row * spacing_y + column * slope) * unit);
            var location = WallLayout.locate(room, target, anchor, Math.Clamp(layer - column, 0, room.plan.Length));
            result.Add(new(available[cells[i].id], location));
        }
        return result;
    }

    public void shift(RoomView room, int x, int y)
    {
        if (start is not { } anchor) return;
        double unit = 64.0 / room.plan.Scale;
        start = WallLayout.locate(room, WallLayout.project(room, anchor) + new Avalonia.Vector(x * unit, y * unit), anchor);
    }

    public void turn()
    {
        if (start is { } anchor)
            start = new(anchor.Wall, anchor.Offset, anchor.Orientation.IsRight ? WallOrientation.Left : WallOrientation.Right);
    }

    public void infer(RoomView room)
    {
        var available = room.items.ToDictionary(x => x.id);
        var first = cells.OrderBy(x => x.row).ThenBy(x => x.column).FirstOrDefault(x => available.ContainsKey(x.id));
        if (first is null) return;
        start = available[first.id].location;
        var origin = WallLayout.project(room, start.Value);
        double unit = 64.0 / room.plan.Scale;
        var right = cells.Where(x => x.row == first.row && x.column > first.column && available.ContainsKey(x.id)).OrderBy(x => x.column).FirstOrDefault();
        if (right is not null)
        {
            var delta = WallLayout.project(room, available[right.id].location) - origin;
            int distance = right.column - first.column;
            spacing_x = Math.Clamp((int)Math.Round(delta.X / distance / unit), 1, 9999);
            slope = Math.Clamp((int)Math.Round(delta.Y / distance / unit), -9999, 9999);
        }
        var down = cells.Where(x => x.column == first.column && x.row > first.row && available.ContainsKey(x.id)).OrderBy(x => x.row).FirstOrDefault();
        if (down is not null)
            spacing_y = Math.Clamp((int)Math.Round((WallLayout.project(room, available[down.id].location).Y - origin.Y) / (down.row - first.row) / unit), 1, 9999);
    }

    public static GridGroup[] groups(RoomView room) => room.items.GroupBy(x => x.id.ToString() is var id && id.Length > 5 ? id[..5] : x.id.ToString())
        .Where(x => x.Count() >= 3).Select(x => new GridGroup($"{x.Key}… ({x.Count()})", x.OrderBy(x => x.id).Select(x => x.id).ToArray())).ToArray();

    public static Id[] parse_ids(string text)
    {
        if (text.Length > 1_000_000) throw new ArgumentException("The ID list is too large.");
        return text.Split(['\r', '\n', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Id.TryParse(x, out var id) && (long)id != 0 ? id : throw new FormatException($"Invalid furniture ID: {x}"))
            .Distinct().ToArray();
    }

    public static WallGrid load(string text, RoomView room)
    {
        if (text.Length > 4_000_000) throw new ArgumentException("The grid file is too large.");
        var grid = new WallGrid { custom_cells = true };
        var values = new Dictionary<string, int>();
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.TrimStart().StartsWith('#'))
            {
                foreach (string part in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = part.Split('=');
                    if (pair.Length == 2 && int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) values[pair[0]] = value;
                }
                continue;
            }
            var fields = line.Split('\t');
            if (fields.Length < 3 || !Id.TryParse(fields[0], out var id) || (long)id == 0 ||
                !int.TryParse(fields[1], out int column) || !int.TryParse(fields[2], out int row) || column < 0 || row < 0)
                throw new FormatException("Each grid row needs an ID, column and row separated by tabs.");
            WallLocation? saved = fields.Length >= 4 && !string.IsNullOrWhiteSpace(fields[3]) ? WallLocation.ParseString(fields[3]) : (WallLocation?)null;
            grid.cells.Add(new(id, column, row, saved));
        }
        if (grid.cells.Count == 0 || grid.cells.Count > 20000) throw new FormatException("The grid needs between 1 and 20,000 items.");
        if (grid.cells.Select(x => x.id).Distinct().Count() != grid.cells.Count) throw new FormatException("The grid contains duplicate furniture IDs.");
        grid.columns = values.GetValueOrDefault("cols", grid.cells.Max(x => x.column) + 1);
        grid.rows = values.GetValueOrDefault("rows", grid.cells.Max(x => x.row) + 1);
        double ratio = (double)room.plan.Scale / Math.Max(1, values.GetValueOrDefault("scale", room.plan.Scale));
        grid.spacing_x = Math.Max(1, (int)Math.Round(values.GetValueOrDefault("spacingx", 8) * ratio));
        grid.spacing_y = Math.Max(1, (int)Math.Round(values.GetValueOrDefault("spacingy", 8) * ratio));
        grid.slope = (int)Math.Round(values.GetValueOrDefault("offset", 0) * ratio);
        grid.layer = values.GetValueOrDefault("layer", 0);
        var first = grid.cells.OrderBy(x => x.row).ThenBy(x => x.column).First();
        grid.start = room.items.FirstOrDefault(x => x.id == first.id)?.location ?? first.saved;
        if (!values.ContainsKey("spacingx") || !values.ContainsKey("spacingy")) grid.infer(room);
        grid.validate();
        return grid;
    }

    public string save(RoomView room)
    {
        var current = room.items.ToDictionary(x => x.id, x => x.location);
        var target = moves(room).ToDictionary(x => x.before.id, x => x.after);
        var text = new StringBuilder();
        text.AppendLine($"# wga cols={columns} rows={rows} spacingx={spacing_x} spacingy={spacing_y} offset={slope} layer={layer} scale={room.plan.Scale}");
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            int column = custom_cells ? cell.column : i % columns;
            int row = custom_cells ? cell.row : i / columns;
            text.AppendLine($"{cell.id}\t{column}\t{row}\t{target.GetValueOrDefault(cell.id, cell.saved ?? current[cell.id])}");
        }
        return text.ToString();
    }

    void validate()
    {
        if (columns is < 1 or > 20000 || rows is < 1 or > 20000 || (long)columns * rows > 20000)
            throw new ArgumentException("Choose grid dimensions between 1 and 20,000 total cells.");
        if (spacing_x is < 1 or > 9999 || spacing_y is < 1 or > 9999 || Math.Abs((long)slope) > 9999 || Math.Abs((long)layer) > 9999)
            throw new ArgumentException("Grid spacing or layer is outside its supported range.");
    }
}
