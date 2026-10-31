using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Qx;
using Qx.Model;
using WallAligner.Core;
using Point = Avalonia.Point;

namespace WallAligner;

public sealed class WallCanvas : Control
{
    static readonly IBrush overlay_input = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
    enum Gesture { None, Pan, Select, Item, Drawing, Resize, Rotate, Stroke }
    public IWallSession? session;
    public readonly HashSet<Id> selected = [];
    public readonly ObservableCollection<WallDrawing> drawings = [];
    public readonly HashSet<WallDrawing> selected_drawings = [];
    public WallDrawing? active_drawing { get; private set; }
    public WallDrawing? draft { get; private set; }
    public event Action<IReadOnlyList<WallMove>>? moved;
    public event Action? changed;
    public event Action? regions_changed;
    public event Action? preview_changed;
    public event Action? layer_changed;
    public event Action<double>? pattern_resized;
    public event Action<double>? pattern_rotated;
    public event Action<WallDrawing?>? drawing_selected;
    public event Action<WallDrawing>? text_edit_requested;
    public event Action? edit_started;
    public event Action<bool>? edit_finished;
    public WallDrawing? editing_text;
    public event Action<IReadOnlyList<Point[]>>? drawn;
    public bool overlay;
    public Func<KeyModifiers>? pointer_modifiers;
    public bool capture;
    public bool front;
    public bool drawing;
    public bool locked;
    public bool selecting_items = true;
    public bool pick_grid;
    public event Action<Point>? grid_picked;
    public IReadOnlyList<WallMove> grid_moves = [];
    public Rect obstruction;
    public int step = 1;
    public double zoom = 1;
    public long revision { get; private set; }
    public Point center;
    public IReadOnlyList<Point> pattern => active_drawing?.points ?? [];
    public IReadOnlyList<WallEntry> pattern_items => active_drawing?.items ?? [];
    public Point pattern_origin
    {
        get => active_drawing?.origin ?? default;
        set { if (active_drawing is { } item) item.origin = value; }
    }
    readonly Dictionary<Id, WallLocation> dragging = [];
    readonly Dictionary<Id, WallLocation> pending = [];
    readonly Dictionary<Id, WallLocation> drawing_targets = [];
    readonly Dictionary<Id, int> layers = [];
    readonly Dictionary<Id, WallLocation> moving = [];
    readonly HashSet<Id> confirmed = [];
    public string movement_text = "";
    public double movement_progress;
    public int moved_count => confirmed.Count;
    IReadOnlyList<WallEntry> drag_items = [];
    readonly List<Point> stroke = [];
    Gesture gesture;
    Point start;
    Point pan_center;
    double pan_zoom;
    bool external_pan;
    bool editing;
    Point resize_anchor;
    Vector resize_vector;
    Point rotation_center;
    double rotation_start;
    Rect move_bounds;
    double[] snap_x = [];
    double[] snap_y = [];
    double? guide_x;
    double? guide_y;
    double? guide_angle;
    readonly Dictionary<WallDrawing, (Point origin, Point[] points, Point[][] outline, double width, double rotation)> originals = [];
    Rect selection;
    bool selection_additive;
    long room_generation = -1;
    long room_id = -1;
    FloorPlan? floor_plan;
    StreamGeometry? floor_geometry;

    public WallCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        PointerPressed += pressed;
        PointerMoved += motion;
        PointerReleased += released;
        PointerWheelChanged += wheel;
        PointerCaptureLost += (_, _) => { if (!external_pan) reset_drag(); };
    }

    public void refresh()
    {
        var room = session?.room;
        if (room is null || room.generation != room_generation || room.id != room_id)
        {
            room_generation = room?.generation ?? -1;
            room_id = room?.id ?? -1;
            selected.Clear();
            drawings.Clear();
            selected_drawings.Clear();
            active_drawing = null;
            draft = null;
            grid_moves = [];
            pending.Clear();
            layers.Clear();
            clear_movement();
            reset_drag();
            drawing_selected?.Invoke(null);
            zoom = 1;
            center_view();
        }
        if (room is not null && selected.Count > 0)
        {
            var present = room.items.Select(x => x.id).ToHashSet();
            selected.RemoveWhere(id => !present.Contains(id));
        }
        if (room is not null && layers.Count > 0)
        {
            var current = room.items.ToDictionary(item => item.id);
            foreach (var id in layers.Keys.Where(id => !current.TryGetValue(id, out var item) || item.location.Wall.Y == layers[id]).ToArray())
                layers.Remove(id);
            prune_layers();
        }
        redraw();
    }

    public Point screen(Point point) => new((point.X - center.X) * zoom + Bounds.Width / 2,
        (point.Y - center.Y) * zoom + Bounds.Height / 2);
    public Point world(Point point) => new((point.X - Bounds.Width / 2) / zoom + center.X,
        (point.Y - Bounds.Height / 2) / zoom + center.Y);

    public Point position(WallEntry item)
    {
        if (session?.room is not { } room) return default;
        return WallLayout.project(room, placement(item));
    }

    WallLocation placement(WallEntry item) =>
        dragging.TryGetValue(item.id, out var target) || drawing_targets.TryGetValue(item.id, out target) || pending.TryGetValue(item.id, out target)
            ? target : item.location;

    public void drawings_changed()
    {
        preview_changed?.Invoke();
        redraw();
    }

    public void select_drawing(WallDrawing? item, bool additive = false)
    {
        if (item is not null && !drawings.Contains(item) && !ReferenceEquals(item, draft)) throw new ArgumentException("The drawing is not in this room.");
        if (!additive) selected_drawings.Clear();
        if (item is not null)
        {
            if (additive && !selected_drawings.Add(item)) selected_drawings.Remove(item);
            else selected_drawings.Add(item);
        }
        active_drawing = item is not null && selected_drawings.Contains(item) ? item : selected_drawings.LastOrDefault();
        selected.Clear();
        prune_layers();
        drawing_selected?.Invoke(active_drawing);
        redraw();
    }

    public void select_drawings(IEnumerable<WallDrawing> items)
    {
        var selection = items.Where(drawings.Contains).ToArray();
        selected_drawings.Clear();
        foreach (var item in selection) selected_drawings.Add(item);
        active_drawing = selection.LastOrDefault();
        selected.Clear();
        prune_layers();
        drawing_selected?.Invoke(active_drawing);
        redraw();
    }

    public void move_selected(Vector delta)
    {
        foreach (var item in selected_drawings) item.origin += delta;
        preview_changed?.Invoke();
        redraw();
    }

    public void center_view()
    {
        if (session?.room is not { } room) return;
        var points = room.items.Select(x => WallLayout.project(room, x.location)).ToList();
        for (int y = 0; y < room.plan.Length; y++)
            for (int x = 0; x < room.plan.Width; x++)
                if (room.plan[x, y] >= 0)
                    points.Add(new Point((x - y) * 32, (x + y) * 16 - room.plan[x, y] * 32));
        if (points.Count > 0) center = bounds_of(points).Center;
        redraw();
    }

    public void select_all(int? kind = null)
    {
        active_drawing = null;
        selected_drawings.Clear();
        drawing_selected?.Invoke(null);
        selected.Clear();
        if (session?.room is { } room)
            foreach (var item in room.items.Where(x => kind is null || x.kind == kind)) selected.Add(item.id);
        prune_layers();
        redraw();
    }

    public IReadOnlyList<WallEntry> chosen => session?.room?.items.Where(x => selected.Contains(x.id)).ToArray() ?? [];

    public int layer(WallEntry item) => layers.GetValueOrDefault(item.id, item.location.Wall.Y);

    public void set_layer(int value)
    {
        foreach (var item in chosen)
        {
            if (item.location.Wall.Y == value) layers.Remove(item.id);
            else layers[item.id] = value;
        }
        layer_changed?.Invoke();
        redraw();
    }

    public void begin_drawing(WallDrawing item)
    {
        draft = item;
        select_drawing(item);
    }

    public void complete_drawing()
    {
        if (draft is not { } item) return;
        drawings.Add(item);
        draft = null;
        select_drawing(item);
    }

    public void begin_movement(IReadOnlyList<WallMove> moves)
    {
        moving.Clear();
        confirmed.Clear();
        foreach (var move in moves) moving[move.before.id] = move.after;
        movement_progress = 0;
        movement_text = $"Moving 0 / {moving.Count}";
        redraw();
    }

    public void confirm_movement(WallMove move)
    {
        if (moving.ContainsKey(move.before.id)) confirmed.Add(move.before.id);
        redraw();
    }

    public void clear_movement()
    {
        moving.Clear();
        confirmed.Clear();
        movement_text = "";
        movement_progress = 0;
    }

    void prune_layers()
    {
        foreach (var id in layers.Keys.Where(id => !selected.Contains(id)).ToArray()) layers.Remove(id);
    }

    public bool has_layer_preview => layers.Keys.Any(selected.Contains);

    public IReadOnlyList<WallMove> layer_moves => layers.Count > 0 && session?.room is { } room
        ? room.items.Where(item => selected.Contains(item.id) && layers.ContainsKey(item.id)).Select(item => new WallMove(item,
            WallLayout.locate(room, WallLayout.project(room, item.location), item.location, layer(item)))).ToArray()
        : [];

    public IReadOnlyList<WallMove> transform(Func<Point, int, Point> map)
    {
        if (session?.room is not { } room) return [];
        return chosen.Select((item, index) => new WallMove(item,
            WallLayout.locate(room, map(position(item), index), item.location, layer(item)))).ToArray();
    }

    public IReadOnlyList<WallMove> pattern_moves() => drawing_moves(drawings);
    public IReadOnlyList<WallMove> selected_moves() => drawing_moves(selected_drawings);

    IReadOnlyList<WallMove> drawing_moves(IEnumerable<WallDrawing> selection)
    {
        if (session?.room is not { } room) return [];
        var current = room.items.ToDictionary(x => x.id);
        return selection.SelectMany(item => item.items.Zip(item.points, (entry, point) =>
            current.TryGetValue(entry.id, out var latest)
                ? new WallMove(latest, layers.TryGetValue(latest.id, out int value)
                    ? WallLayout.locate(room, item.origin + (Vector)point, latest.location, value)
                    : WallLayout.locate(room, item.origin + (Vector)point, latest.location, front)) : null))
            .OfType<WallMove>().ToArray();
    }

    public IReadOnlyList<WallMove> preview_moves => dragging.Count > 0
        ? drag_items.Select(x => new WallMove(x, dragging.GetValueOrDefault(x.id, x.location))).ToArray()
        : (pending.Count > 0 || drawing_targets.Count > 0) && session?.room is { } room
            ? room.items.Where(x => pending.ContainsKey(x.id) || drawing_targets.ContainsKey(x.id))
                .Select(x => new WallMove(x, placement(x))).ToArray()
            : drawing_moves(selected_drawings);
    public bool item_preview => dragging.Count > 0 || pending.Count > 0 || drawing_targets.Count > 0;

    public void present(IReadOnlyList<WallMove> moves)
    {
        pending.Clear();
        foreach (var move in moves) pending[move.before.id] = move.after;
        redraw();
    }

    public void revise_positions(IReadOnlyList<WallMove> moves)
    {
        foreach (var move in moves) pending[move.before.id] = move.after;
        redraw();
    }

    public void finish_move() { pending.Clear(); redraw(); }
    public void actual_size() => set_zoom(false);
    public double actual_zoom => zoom;
    public void set_zoom(bool zoomed_out) { zoom = zoomed_out ? .5 : 1; redraw(); }

    public void begin_pan(Point point, bool external = false)
    {
        start = point;
        pan_center = center;
        pan_zoom = zoom;
        external_pan = external;
        gesture = Gesture.Pan;
    }

    public void move_pan(Point point)
    {
        if (gesture != Gesture.Pan) return;
        center = pan_center - (point - start) / pan_zoom;
        redraw();
    }

    public void end_pan() { external_pan = false; gesture = Gesture.None; redraw(); }

    public void deselect_at(Point point)
    {
        if (interacting || session?.room is null) return;
        if (hit_regions().Any(area => area.Contains(point))) return;
        select_drawing(null);
    }

    Rect box(WallDrawing item) => new Rect(screen(item.origin + (Vector)item.bounds.TopLeft),
        new Size(item.bounds.Width * zoom, item.bounds.Height * zoom)).Inflate(14);
    public bool transformable => selected_drawings.Any(x => x.outline.Length > 0) || selected.Count > 1;
    public Rect pattern_box => selected_drawings.Count > 0
        ? bounds_of(selected_drawings.SelectMany(item => new[] { box(item).TopLeft, box(item).BottomRight }))
        : selected.Count > 1 ? bounds_of(chosen.Select(item => screen(position(item)))).Inflate(14) : default;
    Point[] handles()
    {
        var area = pattern_box;
        return [area.TopLeft, new(area.Center.X, area.Top), area.TopRight, new(area.Right, area.Center.Y),
            area.BottomRight, new(area.Center.X, area.Bottom), area.BottomLeft, new(area.Left, area.Center.Y)];
    }

    Point rotation_handle => new(pattern_box.Center.X, pattern_box.Top - 24);

    public void clear_pattern()
    {
        if (draft is not null && selected_drawings.Contains(draft)) draft = null;
        foreach (var item in selected_drawings.ToArray()) drawings.Remove(item);
        select_drawing(null);
        drawings_changed();
    }

    public void redraw() { revision++; InvalidateVisual(); changed?.Invoke(); regions_changed?.Invoke(); }

    public static Rect bounds_of(IEnumerable<Point> points)
    {
        var array = points.ToArray();
        return array.Length == 0 ? default : new Rect(new Point(array.Min(p => p.X), array.Min(p => p.Y)),
            new Point(array.Max(p => p.X), array.Max(p => p.Y)));
    }

    public IEnumerable<Rect> hit_regions()
    {
        if (session?.room is { } room)
            foreach (var item in room.items)
            {
                var point = screen(position(item));
                yield return new Rect(point.X - 11, point.Y - 11, 22, 22);
            }
        foreach (var item in drawings.Where(x => x.outline.Length > 0)) yield return box(item).Inflate(16);
        if (transformable)
        {
            yield return pattern_box.Inflate(16);
            yield return new Rect(rotation_handle.X - 12, rotation_handle.Y - 12, 24, 24);
        }
    }

    public bool interacting => gesture != Gesture.None;

    void pressed(object? sender, PointerPressedEventArgs e)
    {
        if (external_pan) return;
        var modifiers = e.KeyModifiers | (overlay ? pointer_modifiers?.Invoke() ?? KeyModifiers.None : KeyModifiers.None);
        Focus();
        var point = e.GetPosition(this);
        var buttons = e.GetCurrentPoint(this).Properties;
        if (buttons.IsLeftButtonPressed && locked) return;
        start = point;
        var active = active_drawing;
        int corner = transformable
            ? Array.FindIndex(handles(), h => ((Vector)(h - point)).Length <= 14) : -1;
        bool over_rotation = transformable && ((Vector)(point - rotation_handle)).Length <= 12;
        bool additive = (modifiers & (KeyModifiers.Alt | KeyModifiers.Control)) != 0;
        var hit = session?.room?.items.Where(x => ((Vector)(screen(position(x)) - point)).Length <= 11)
            .OrderBy(x => ((Vector)(screen(position(x)) - point)).Length).FirstOrDefault();
        var drawing_hit = drawings.LastOrDefault(x => x.outline.Length > 0 && box(x).Contains(point));
        if (buttons.IsLeftButtonPressed && !additive && e.ClickCount == 2 && corner < 0 && !over_rotation &&
            drawings.LastOrDefault(item => item.tool == "text" && box(item).Contains(point)) is { } text)
        {
            select_drawing(text);
            text_edit_requested?.Invoke(text);
            e.Handled = true;
            return;
        }
        if (buttons.IsMiddleButtonPressed || buttons.IsRightButtonPressed) begin_pan(point);
        else if (buttons.IsLeftButtonPressed && pick_grid)
        {
            pick_grid = false;
            grid_picked?.Invoke(world(point));
        }
        else if (buttons.IsLeftButtonPressed && !additive && over_rotation)
        {
            gesture = Gesture.Rotate;
            originals.Clear();
            foreach (var item in selected_drawings) originals[item] = (item.origin, item.points, item.outline, item.width, item.rotation);
            if (selected_drawings.Count == 0) begin_item_drag();
            rotation_center = world(pattern_box.Center);
            var offset = world(point) - rotation_center;
            rotation_start = Math.Atan2(offset.Y, offset.X);
        }
        else if (buttons.IsLeftButtonPressed && !additive && corner >= 0)
        {
            gesture = Gesture.Resize;
            originals.Clear();
            foreach (var item in selected_drawings) originals[item] = (item.origin, item.points, item.outline, item.width, item.rotation);
            if (selected_drawings.Count == 0) begin_item_drag();
            resize_anchor = world(handles()[(corner + 4) % 8]);
            resize_vector = world(point) - resize_anchor;
        }
        else if (buttons.IsLeftButtonPressed && !additive && selected.Count > 1 && pattern_box.Contains(point) &&
            (hit is null || selected.Contains(hit.id)))
        {
            begin_item_drag();
            gesture = Gesture.Item;
        }
        else if (buttons.IsLeftButtonPressed && hit is not null && (selecting_items || additive))
            select_item(hit, additive);
        else if (buttons.IsLeftButtonPressed && (modifiers.HasFlag(KeyModifiers.Shift) || additive && drawing_hit is null))
        {
            gesture = Gesture.Select;
            selection = new Rect(point, point);
            selection_additive = additive;
        }
        else if (buttons.IsLeftButtonPressed && drawing && !additive)
        {
            gesture = Gesture.Stroke;
            stroke.Clear();
            stroke.Add(world(point));
        }
        else if (buttons.IsLeftButtonPressed)
        {
            var item = drawing_hit;
            if (item is not null)
            {
                if (additive || !selected_drawings.Contains(item)) select_drawing(item, additive);
                if (selected_drawings.Contains(item))
                {
                    gesture = Gesture.Drawing;
                    originals.Clear();
                    foreach (var drawing in selected_drawings) originals[drawing] = (drawing.origin, drawing.points, drawing.outline, drawing.width, drawing.rotation);
                    prepare_snaps();
                }
            }
            else if (session?.room is { } room)
            {
                if (hit is null) select_drawing(null);
                else select_item(hit, additive);
            }
        }
        if (gesture is Gesture.Item or Gesture.Drawing or Gesture.Resize or Gesture.Rotate) begin_edit();
        if (interacting) e.Pointer.Capture(this);
        redraw();
        e.Handled = true;
    }

    void select_item(WallEntry item, bool additive)
    {
        active_drawing = null;
        selected_drawings.Clear();
        if (additive)
        {
            if (!selected.Add(item.id)) selected.Remove(item.id);
        }
        else
        {
            if (!selected.Contains(item.id)) selected.Clear();
            selected.Add(item.id);
        }
        prune_layers();
        drawing_selected?.Invoke(null);
        begin_item_drag();
        if (dragging.Count > 0) gesture = Gesture.Item;
    }

    void begin_item_drag()
    {
        dragging.Clear();
        drag_items = locked || session?.room?.can_edit != true ? []
            : chosen.Select(item => pending.TryGetValue(item.id, out var location) ? item with { location = location } : item).ToArray();
        foreach (var entry in drag_items) dragging[entry.id] = entry.location;
    }

    public void drag_drawing(WallDrawing item, Point point, IPointer pointer)
    {
        if (locked) return;
        if (!selected_drawings.Contains(item)) select_drawing(item);
        start = point;
        gesture = Gesture.Drawing;
        originals.Clear();
        foreach (var drawing in selected_drawings) originals[drawing] = (drawing.origin, drawing.points, drawing.outline, drawing.width, drawing.rotation);
        prepare_snaps();
        begin_edit();
        pointer.Capture(this);
    }

    void begin_edit()
    {
        if (editing) return;
        editing = true;
        edit_started?.Invoke();
    }

    void motion(object? sender, PointerEventArgs e)
    {
        if (external_pan) return;
        var point = e.GetPosition(this);
        var active = active_drawing;
        switch (gesture)
        {
            case Gesture.Pan: center = pan_center - (point - start) / pan_zoom; break;
            case Gesture.Select: selection = new Rect(start, point).Normalize(); break;
            case Gesture.Stroke:
                var next = world(point);
                if (stroke.Count < 20000 && ((Vector)(next - stroke[^1])).Length > 2 / zoom) stroke.Add(next);
                break;
            case Gesture.Resize when active is not null || drag_items.Count > 1:
                var delta = world(point) - resize_anchor;
                double denominator = resize_vector.X * resize_vector.X + resize_vector.Y * resize_vector.Y;
                double scale = (delta.X * resize_vector.X + delta.Y * resize_vector.Y) / Math.Max(1, denominator);
                scale = Math.Clamp(scale, originals.Count > 0 ? originals.Values.Max(x => 1 / Math.Max(1, x.width)) : .01,
                    originals.Count > 0 ? originals.Values.Min(x => 10000 / Math.Max(1, x.width)) : 100);
                foreach (var entry in originals)
                {
                    entry.Key.points = entry.Value.points.Select(p => new Point(p.X * scale, p.Y * scale)).ToArray();
                    entry.Key.outline = entry.Value.outline.Select(c => c.Select(p => new Point(p.X * scale, p.Y * scale)).ToArray()).ToArray();
                    entry.Key.refresh_geometry();
                    entry.Key.origin = resize_anchor + (entry.Value.origin - resize_anchor) * scale;
                    entry.Key.width = entry.Value.width * scale;
                }
                if (active is not null) pattern_resized?.Invoke(active.width);
                else if (session?.room is { } resize_room)
                    foreach (var entry in drag_items)
                    {
                        var position = WallLayout.project(resize_room, entry.location);
                        dragging[entry.id] = WallLayout.locate(resize_room, resize_anchor + (position - resize_anchor) * scale, entry.location, layer(entry));
                    }
                break;
            case Gesture.Rotate when active is not null || drag_items.Count > 1:
                var offset = world(point) - rotation_center;
                double angle = Math.Atan2(offset.Y, offset.X) - rotation_start;
                double original_angle = active is not null ? originals[active].rotation : 0;
                double degrees = original_angle + angle * 180 / Math.PI;
                double interval = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 15 : 45;
                double nearest = Math.Round(degrees / interval) * interval;
                guide_angle = null;
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Alt) &&
                    (interval == 15 || Math.Abs(nearest - degrees) <= 3))
                {
                    angle = (nearest - original_angle) * Math.PI / 180;
                    guide_angle = (nearest % 360 + 360) % 360;
                }
                Point rotate(Point p) => new(p.X * Math.Cos(angle) - p.Y * Math.Sin(angle), p.X * Math.Sin(angle) + p.Y * Math.Cos(angle));
                foreach (var entry in originals)
                {
                    entry.Key.origin = rotation_center + (Vector)rotate((Point)(entry.Value.origin - rotation_center));
                    entry.Key.points = entry.Value.points.Select(rotate).ToArray();
                    entry.Key.outline = entry.Value.outline.Select(c => c.Select(rotate).ToArray()).ToArray();
                    entry.Key.rotation = (entry.Value.rotation + angle * 180 / Math.PI) % 360;
                    entry.Key.refresh_geometry();
                }
                if (active is not null) pattern_rotated?.Invoke(active.rotation);
                else if (session?.room is { } rotation_room)
                    foreach (var entry in drag_items)
                    {
                        var position = WallLayout.project(rotation_room, entry.location);
                        dragging[entry.id] = WallLayout.locate(rotation_room,
                            rotation_center + (Vector)rotate((Point)(position - rotation_center)), entry.location, layer(entry));
                    }
                break;
            case Gesture.Drawing:
                var shift = align_move((Vector)(point - start) / zoom, e.KeyModifiers);
                foreach (var entry in originals) entry.Key.origin = entry.Value.origin + shift;
                break;
            case Gesture.Item when session?.room is { } room:
                var movement = (Vector)(point - start) / zoom;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    movement = Math.Abs(movement.X) >= Math.Abs(movement.Y) ? new Vector(movement.X, 0) : new Vector(0, movement.Y);
                foreach (var entry in drag_items)
                    dragging[entry.id] = WallLayout.locate(room, WallLayout.project(room, entry.location) + movement, entry.location, layer(entry));
                break;
            default:
                bool over_handle = transformable && handles().Any(h => ((Vector)(h - point)).Length <= 14);
                bool over_rotation = transformable && ((Vector)(point - rotation_handle)).Length <= 12;
                Cursor = over_rotation ? new Cursor(StandardCursorType.Hand) : over_handle ? new Cursor(StandardCursorType.TopLeftCorner) : null;
                ToolTip.SetTip(this, over_rotation ? "Drag to rotate" : over_handle ? "Drag to resize" : null);
                return;
        }
        if (gesture is Gesture.Resize or Gesture.Rotate or Gesture.Drawing or Gesture.Item)
        {
            drawing_targets.Clear();
            if (selected_drawings.Count > 0)
                foreach (var move in drawing_moves(selected_drawings)) drawing_targets[move.before.id] = move.after;
            preview_changed?.Invoke();
        }
        redraw();
    }

    void released(object? sender, PointerReleasedEventArgs e)
    {
        if (external_pan) return;
        if (gesture == Gesture.Select && session?.room is { } room &&
            (!selection_additive || selection.Width > 3 || selection.Height > 3))
        {
            var objects = drawings.Where(x => x.outline.Length > 0 && selection.Intersects(box(x))).ToArray();
            if (!selecting_items && objects.Length > 0)
                select_drawings(selection_additive ? selected_drawings.Concat(objects).ToArray() : objects);
            else
            {
                if (!selection_additive)
                {
                    selected.Clear();
                    selected_drawings.Clear();
                    active_drawing = null;
                }
                foreach (var item in room.items)
                    if (selection.Contains(screen(position(item)))) selected.Add(item.id);
                if (selected.Count > 0)
                {
                    selected_drawings.Clear();
                    active_drawing = null;
                }
                prune_layers();
                drawing_selected?.Invoke(active_drawing);
            }
        }
        if (gesture == Gesture.Stroke && stroke.Count > 1) drawn?.Invoke([stroke.ToArray()]);
        if (gesture is Gesture.Item or Gesture.Rotate or Gesture.Resize && drag_items.Count > 0)
        {
            var moves = drag_items.Select(x => new WallMove(x, dragging.GetValueOrDefault(x.id, x.location)))
                .Where(x => x.before.location != x.after).ToArray();
            if (moves.Length > 0) { present(moves); moved?.Invoke(moves); }
        }
        reset_drag(true);
        e.Pointer.Capture(null);
        redraw();
    }

    public void cancel_edit()
    {
        if (gesture is Gesture.Drawing or Gesture.Resize or Gesture.Rotate)
            foreach (var entry in originals)
            {
                entry.Key.origin = entry.Value.origin;
                entry.Key.points = entry.Value.points;
                entry.Key.outline = entry.Value.outline;
                entry.Key.refresh_geometry();
                entry.Key.width = entry.Value.width;
                entry.Key.rotation = entry.Value.rotation;
            }
        reset_drag();
        select_drawing(null);
        preview_changed?.Invoke();
    }

    void reset_drag(bool accepted = false)
    {
        bool was_editing = editing;
        editing = false;
        if (accepted)
            foreach (var target in drawing_targets) pending[target.Key] = target.Value;
        drawing_targets.Clear();
        dragging.Clear(); drag_items = []; stroke.Clear(); originals.Clear();
        snap_x = []; snap_y = [];
        guide_x = guide_y = guide_angle = null;
        external_pan = false; gesture = Gesture.None;
        preview_changed?.Invoke();
        redraw();
        if (was_editing) edit_finished?.Invoke(accepted);
    }

    void prepare_snaps()
    {
        move_bounds = bounds_of(originals.SelectMany(entry => entry.Value.outline.SelectMany(points => points)
            .Select(point => entry.Value.origin + (Vector)point)));
        var targets = drawings.Where(item => !selected_drawings.Contains(item) && item.outline.Length > 0)
            .Select(item => bounds_of(item.outline.SelectMany(points => points).Select(point => item.origin + (Vector)point))).ToArray();
        snap_x = targets.SelectMany(bounds => new[] { bounds.Left, bounds.Center.X, bounds.Right }).ToArray();
        snap_y = targets.SelectMany(bounds => new[] { bounds.Top, bounds.Center.Y, bounds.Bottom }).ToArray();
    }

    Vector align_move(Vector movement, KeyModifiers modifiers)
    {
        guide_x = guide_y = null;
        if (modifiers.HasFlag(KeyModifiers.Alt)) return movement;
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            if (Math.Abs(movement.X) >= Math.Abs(movement.Y)) { guide_y = move_bounds.Center.Y; return new(movement.X, 0); }
            guide_x = move_bounds.Center.X;
            return new(0, movement.Y);
        }
        double tolerance = 5 / zoom;
        double adjust_x = tolerance, adjust_y = tolerance;
        ReadOnlySpan<double> x_anchors = [move_bounds.Left, move_bounds.Center.X, move_bounds.Right];
        ReadOnlySpan<double> y_anchors = [move_bounds.Top, move_bounds.Center.Y, move_bounds.Bottom];
        foreach (double target in snap_x)
            foreach (double anchor in x_anchors)
            {
                double difference = target - anchor - movement.X;
                if (Math.Abs(difference) >= Math.Abs(adjust_x)) continue;
                adjust_x = difference; guide_x = target;
            }
        foreach (double target in snap_y)
            foreach (double anchor in y_anchors)
            {
                double difference = target - anchor - movement.Y;
                if (Math.Abs(difference) >= Math.Abs(adjust_y)) continue;
                adjust_y = difference; guide_y = target;
            }
        if (guide_x.HasValue) movement = new(movement.X + adjust_x, movement.Y);
        else if (Math.Abs(movement.X) < tolerance && Math.Abs(movement.Y) > tolerance * 2)
        { movement = new(0, movement.Y); guide_x = move_bounds.Center.X; }
        if (guide_y.HasValue) movement = new(movement.X, movement.Y + adjust_y);
        else if (Math.Abs(movement.Y) < tolerance && Math.Abs(movement.X) > tolerance * 2)
        { movement = new(movement.X, 0); guide_y = move_bounds.Center.Y; }
        return movement;
    }

    void wheel(object? sender, PointerWheelEventArgs e)
    {
        var point = e.GetPosition(this);
        if (active_drawing is { outline.Length: > 0 } item && box(item).Inflate(16).Contains(point))
        {
            double scale = Math.Clamp(Math.Pow(1.1, e.Delta.Y), selected_drawings.Max(x => 1 / Math.Max(1, x.width)),
                selected_drawings.Min(x => 10000 / Math.Max(1, x.width)));
            var anchor = world(pattern_box.Center);
            foreach (var selected_item in selected_drawings)
            {
                selected_item.points = selected_item.points.Select(p => new Point(p.X * scale, p.Y * scale)).ToArray();
                selected_item.outline = selected_item.outline.Select(c => c.Select(p => new Point(p.X * scale, p.Y * scale)).ToArray()).ToArray();
                selected_item.refresh_geometry();
                selected_item.origin = anchor + (selected_item.origin - anchor) * scale;
                selected_item.width *= scale;
            }
            pattern_resized?.Invoke(item.width);
            preview_changed?.Invoke();
        }
        else
        {
            var fixed_point = world(point);
            zoom = e.Delta.Y < 0 ? .5 : 1;
            center += fixed_point - world(point);
        }
        redraw(); e.Handled = true;
    }

    public void render_floor(DrawingContext context)
    {
        if (session?.room is not { } room) return;
        if (!ReferenceEquals(floor_plan, room.plan))
        {
            floor_plan = room.plan;
            floor_geometry = new StreamGeometry();
            using var path = floor_geometry.Open();
            for (int y = 0; y < room.plan.Length; y++)
                for (int x = 0; x < room.plan.Width; x++)
                {
                    if (room.plan[x, y] < 0) continue;
                    var origin = new Point((x - y) * 32, (x + y) * 16 - room.plan[x, y] * 32);
                    path.BeginFigure(origin + new Vector(0, -16), false);
                    path.LineTo(origin + new Vector(32, 0)); path.LineTo(origin + new Vector(0, 16));
                    path.LineTo(origin + new Vector(-32, 0)); path.EndFigure(true);
                }
        }
        if (floor_geometry is not { } geometry) return;
        var matrix = Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateScale(zoom, zoom) *
            Matrix.CreateTranslation(Bounds.Width / 2, Bounds.Height / 2);
        using (context.PushTransform(matrix))
            context.DrawGeometry(null, new Pen(Brush.Parse(overlay ? "#789287" : "#465149"), .75 / zoom), geometry);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(overlay ? overlay_input : Brush.Parse("#D9303733"), null, new Rect(Bounds.Size));
        if (session?.room is null)
        {
            var text = new FormattedText("Open a room to start aligning", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 18, Brush.Parse("#D6DDD6"));
            context.DrawText(text, new Point((Bounds.Width - text.Width) / 2, Bounds.Height / 2));
        }
        else if (!overlay) render_scene(context);
    }

    public void render_scene(DrawingContext context)
    {
        if (session?.room is not { } room) return;
        render_floor(context);
        var normal = Brush.Parse("#F2F3EA");
        var active = Brush.Parse("#F2C464");
        var outline = new Pen(Brush.Parse("#18251D"), 2);
        foreach (var item in room.items)
        {
            var point = screen(position(item));
            bool chosen = selected.Contains(item.id);
            if (moving.TryGetValue(item.id, out var target))
            {
                var destination = point;
                var actual = screen(WallLayout.project(room, item.location));
                bool done = confirmed.Contains(item.id) && item.location == target && placement(item) == item.location;
                var color = Brush.Parse(done ? "#83D6A3" : "#F2C464");
                if (!done && ((Vector)(destination - actual)).Length > 2)
                {
                    context.DrawLine(new Pen(Brush.Parse("#66879887"), 1), actual, destination);
                    context.DrawEllipse(normal, outline, actual, 3, 3);
                }
                context.DrawEllipse(done ? color : null, new Pen(color, 2), destination, 6, 6);
                continue;
            }
            context.DrawEllipse(chosen ? active : normal, outline, point, chosen ? 6 : 4.5, chosen ? 6 : 4.5);
            if (chosen) context.DrawEllipse(null, new Pen(active, 1), point, 10, 10);
        }
        foreach (var item in drawings.Where(x => x.outline.Length > 0 && x != editing_text))
        {
            bool chosen = selected_drawings.Contains(item);
            var origin = screen(item.origin);
            using (context.PushTransform(Matrix.CreateScale(zoom, zoom) * Matrix.CreateTranslation(origin.X, origin.Y)))
                if (item.geometry is not null) context.DrawGeometry(null, new Pen(chosen ? active : Brush.Parse("#85C8E8"), 1.5 / zoom), item.geometry);
            for (int i = 0; i < item.points.Length; i++)
                if (i >= item.items.Length || !moving.ContainsKey(item.items[i].id))
                    context.DrawEllipse(chosen ? active : normal, outline, screen(item.origin + (Vector)item.points[i]), 4.5, 4.5);
        }
        foreach (var move in grid_moves)
            context.DrawEllipse(Brush.Parse("#85C8E8"), outline, screen(WallLayout.project(room, move.after)), 4.5, 4.5);
        var guide = new Pen(Brush.Parse("#85C8E8"), 1) { DashStyle = DashStyle.Dash };
        if (guide_x is { } x)
        {
            double sx = screen(new Point(x, 0)).X;
            context.DrawLine(guide, new Point(sx, 0), new Point(sx, Bounds.Height));
        }
        if (guide_y is { } y)
        {
            double sy = screen(new Point(0, y)).Y;
            context.DrawLine(guide, new Point(0, sy), new Point(Bounds.Width, sy));
        }
        if (guide_angle is { } angle)
        {
            var label = new FormattedText($"{angle:0}°", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 12, Brush.Parse("#85C8E8"));
            context.DrawText(label, rotation_handle + new Vector(14, -8));
        }
        if (editing_text is null && transformable)
        {
            context.DrawRectangle(null, new Pen(active, 1) { DashStyle = DashStyle.Dash }, pattern_box);
            foreach (var handle in handles())
                context.DrawRectangle(active, outline, new Rect(handle.X - 6, handle.Y - 6, 12, 12));
            context.DrawLine(new Pen(active, 1), new Point(pattern_box.Center.X, pattern_box.Top), rotation_handle);
            context.DrawEllipse(active, outline, rotation_handle, 6, 6);
        }
        if (gesture == Gesture.Select)
            context.DrawRectangle(Brush.Parse("#253CA275"), new Pen(Brush.Parse("#80CCA6"), 1), selection);
        for (int i = 1; i < stroke.Count; i++)
            context.DrawLine(new Pen(active, 2), screen(stroke[i - 1]), screen(stroke[i]));
    }
}
