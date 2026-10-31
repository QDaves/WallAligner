using System.Globalization;
using Avalonia.Animation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WallAligner.Core;

namespace WallAligner;

public partial class MainWindow : Window
{
    readonly IWallSession session;
    readonly MainView view;
    readonly WallCanvas canvas;
    readonly MoveQueue queue;
    readonly OverlayRegion input_region;
    readonly CancellationTokenSource lifetime = new();
    int preview_revision;
    readonly DispatcherTimer preview_timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer settle_timer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    readonly DispatcherTimer progress_reset = new() { Interval = TimeSpan.FromMilliseconds(180) };
    readonly DispatcherTimer movement_reset = new() { Interval = TimeSpan.FromMilliseconds(1400) };
    readonly DispatcherTimer move_timer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    readonly Dictionary<Qx.Id, WallMove> scheduled_moves = [];
    readonly Transitions fill_transitions = [new DoubleTransition
    {
        Property = ScaleTransform.ScaleXProperty, Duration = TimeSpan.FromMilliseconds(100)
    }];
    readonly OverlayPointer? overlay_pointer;
    readonly Dictionary<string, double> numbers = [];
    OverlayGuide? guide;
    bool client_dirty;
    int drawing_number;
    string tool = "select";
    string furniture_signature = "";
    bool switching;
    bool closed;
    bool closing;
    int refresh_pending;
    long room_generation = -1;
    string layer_selection = "";
    string layer_display = "0";
    bool updating_layer;
    bool layer_dragging;
    bool layer_waiting;
    bool move_paused;
    IReadOnlyList<WallMove>? revised_moves;
    PixelPoint? drag_origin;
    PixelPoint drag_pointer;

    sealed record Furniture(int kind, string name, int count)
    {
        public override string ToString() => $"{name} ({count})";
    }

    T control<T>(string name) where T : Control =>
        view.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing control: {name}");

    public MainWindow(IWallSession session)
    {
        view = new MainView();
        Content = view;
        Title = "WallAligner";
        Width = 1120; Height = 760; MinWidth = 860; MinHeight = 580;
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://WallAligner/WallAligner.png")));
        this.session = session;
        canvas = control<WallCanvas>("canvas");
        canvas.session = session;
        control<ListBox>("objects_list").ItemsSource = canvas.drawings;
        control<ListBox>("objects_list").SelectionChanged += (_, _) =>
        {
            if (!switching) canvas.select_drawings(control<ListBox>("objects_list").SelectedItems?.OfType<WallDrawing>() ?? []);
        };
        canvas.drawing_selected += show_drawing;
        queue = new MoveQueue(session);
        queue.started += movement_started;
        queue.item_confirmed += movement_confirmed;
        movement_reset.Tick += (_, _) =>
        {
            movement_reset.Stop();
            if (queue.busy) return;
            canvas.clear_movement();
            control<TextBlock>("movement_status").Text = "";
            canvas.redraw();
        };
        progress_reset.Tick += (_, _) =>
        {
            progress_reset.Stop();
            if (queue.busy) return;
            control<Border>("move_fill").Opacity = 0;
            var scale = (ScaleTransform)control<Border>("move_fill").RenderTransform!;
            scale.Transitions = null;
            scale.ScaleX = 0;
        };
        input_region = new OverlayRegion(this);
        if (OverlayRegion.available)
        {
            overlay_pointer = new OverlayPointer();
            canvas.pointer_modifiers = () => overlay_pointer.modifiers;
            overlay_pointer.modifiers_changed += update_regions;
            overlay_pointer.pan += (phase, point) => Dispatcher.UIThread.Post(() =>
            {
                if (closed || !canvas.overlay) return;
                var local = canvas.PointToClient(point);
                if (phase == 0) canvas.begin_pan(local, true);
                else if (phase == 1) canvas.move_pan(local);
                else canvas.end_pan();
            });
            overlay_pointer.clicked += point =>
            {
                var modifiers = overlay_pointer.modifiers;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!closed && canvas.overlay && (modifiers & (KeyModifiers.Alt | KeyModifiers.Control)) == 0)
                        canvas.deselect_at(canvas.PointToClient(point));
                });
            };
        }
        canvas.pattern_resized += width =>
        {
            bool previous = switching;
            switching = true;
            control<TextBox>("width_input").Text = Math.Round(width, 1).ToString(CultureInfo.CurrentCulture);
            switching = previous;
        };
        canvas.pattern_rotated += angle =>
        {
            bool previous = switching;
            switching = true;
            control<TextBox>("angle_input").Text = Math.Round(angle, 1).ToString(CultureInfo.CurrentCulture);
            switching = previous;
        };
        canvas.preview_changed += request_preview;
        canvas.edit_started += () =>
        {
            move_timer.Stop();
            move_paused = queue.pause() || move_paused;
            revised_moves = null;
            if (move_paused) control<TextBlock>("movement_status").Text = "Paused";
        };
        canvas.edit_finished += accepted =>
        {
            var changes = accepted ? revised_moves ?? canvas.selected_moves() : null;
            revised_moves = null;
            if (scheduled_moves.Count > 0)
            {
                if (changes is not null) schedule_moves(changes);
                else move_timer.Start();
                return;
            }
            if (!move_paused) return;
            move_paused = false;
            queue.resume(changes);
        };
        move_timer.Tick += (_, _) => flush_moves();
        canvas.layer_changed += request_layer_preview;
        preview_timer.Tick += (_, _) =>
        {
            preview_timer.Stop();
            client_preview();
        };
        settle_timer.Tick += (_, _) =>
        {
            settle_timer.Stop();
            if (canvas.interacting || layer_dragging) return;
            layer_waiting = false;
            client_dirty = true;
            client_preview();
        };
        session.changed += session_changed;
        queue.progress += report;
        canvas.changed += selection_changed;
        canvas.regions_changed += update_regions;
        canvas.moved += moves =>
        {
            if (move_paused || scheduled_moves.Count > 0) revised_moves = moves;
            else if (!queue.busy) _ = run(async () =>
            {
                await queue.apply(moves);
            });
        };
        canvas.drawn += points =>
        {
            var item = canvas.active_drawing;
            if (item is not { tool: "draw", contours.Count: 0 }) item = new_drawing("draw", false);
            item.shape = "Freehand";
            var bounds = WallCanvas.bounds_of(points.SelectMany(x => x));
            item.width = Math.Max(1, bounds.Width);
            item.origin = bounds.Center;
            item.contours = points;
            item.error = null;
            complete_drawing(item);
            show_drawing(item);
            layout_scene();
            canvas.drawing = true;
        };
        var titlebar = control<Border>("titlebar");
        titlebar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
                e.Source is not Control source || source is Button ||
                source.GetVisualAncestors().Any(x => x is Button)) return;
            e.Handled = true;
            if (e.ClickCount == 2) maximize();
            else if (canvas.overlay && WindowState == WindowState.Normal)
            {
                drag_origin = Position;
                drag_pointer = this.PointToScreen(e.GetPosition(this));
                e.Pointer.Capture(titlebar);
            }
            else BeginMoveDrag(e);
        };
        titlebar.PointerMoved += (_, e) =>
        {
            if (drag_origin is not { } origin) return;
            var pointer = this.PointToScreen(e.GetPosition(this));
            Position = new PixelPoint(origin.X + pointer.X - drag_pointer.X, origin.Y + pointer.Y - drag_pointer.Y);
            e.Handled = true;
        };
        titlebar.PointerReleased += (_, e) =>
        {
            if (drag_origin is null || e.InitialPressMouseButton != MouseButton.Left) return;
            drag_origin = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        };
        titlebar.PointerCaptureLost += (_, _) => drag_origin = null;
        bind("close_button", Close);
        bind("settings_button", toggle_settings);
        control<Border>("bottom_edge").PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginResizeDrag(WindowEdge.South, e);
        };
        bind("minimize_button", () => WindowState = WindowState.Minimized);
        bind("maximize_button", maximize);
        control<ToggleButton>("zoom_button").IsCheckedChanged += (_, _) =>
        {
            bool zoomed_out = control<ToggleButton>("zoom_button").IsChecked == true;
            if (canvas.zoom != (zoomed_out ? .5 : 1)) canvas.set_zoom(zoomed_out);
        };
        control<ToggleButton>("pin_button").IsCheckedChanged += (_, _) =>
        {
            Topmost = control<ToggleButton>("pin_button").IsChecked == true;
            if (grid_window is not null) grid_window.Topmost = Topmost;
            guide?.sync(this, canvas.overlay);
        };
        control<Border>("resize_edge").PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginResizeDrag(WindowEdge.East, e);
        };
        bind("center_button", canvas.center_view);
        bind("undo_button", () => _ = run(queue.undo));
        bind("redo_button", () => _ = run(queue.redo));
        bind("new_drawing", () => new_drawing(tool));
        bind("duplicate_drawing", duplicate_drawing);
        bind("clear_preview", () =>
        {
            if (queue.busy) return;
            canvas.clear_pattern();
            layout_scene();
            canvas.drawing = tool == "draw" && control<ComboBox>("shape_input").SelectedIndex == 0;
        });
        bind("preview_button", () => update_preview(true));
        bind("apply_button", () =>
        {
            if (queue.busy || scheduled_moves.Count > 0) { cancel_moves(); return; }
            _ = run(async () =>
            {
            if (canvas.active_drawing is not null) update_preview(false);
            layout_scene();
            var invalid = canvas.drawings.FirstOrDefault(x => x.error is not null || x.placement_error is not null || x.contours.Count == 0);
            if (invalid is not null) throw new InvalidOperationException(invalid.error ?? invalid.placement_error ?? "Finish or remove the empty drawing first.");
            var moves = canvas.pattern_moves();
            if (moves.Count == 0) throw new InvalidOperationException("Create a drawing first.");
            canvas.present(moves);
            await queue.apply(moves);
            });
        });
        bind("open_svg", () => _ = choose_svg());
        bind("nudge_left", () => nudge(-1, 0));
        bind("nudge_right", () => nudge(1, 0));
        bind("nudge_up", () => nudge(0, -1));
        bind("nudge_down", () => nudge(0, 1));
        bind("select_type", () =>
        {
            if (!queue.busy && control<ComboBox>("selection_type").SelectedItem is Furniture choice)
                canvas.select_all(choice.kind);
        });
        control<ComboBox>("selection_type").SelectionChanged += (_, _) => selection_changed();
        foreach (string name in new[] { "select", "text", "svg", "draw" })
            control<ToggleButton>(name + "_tool").Click += (_, _) => choose_tool(name);
        control<ToggleButton>("clickthrough_button").IsEnabled = OverlayRegion.available;
        if (!OverlayRegion.available)
            ToolTip.SetTip(control<ToggleButton>("clickthrough_button"), "Click-through currently requires Windows.");
        control<ToggleButton>("clickthrough_button").IsCheckedChanged += (_, _) => update_overlay();
        control<ToggleButton>("capture_button").IsCheckedChanged += (_, _) =>
        {
            canvas.capture = control<ToggleButton>("capture_button").IsChecked == true;
            update_regions();
        };
        control<CheckBox>("front_input").IsCheckedChanged += (_, _) => canvas.front = control<CheckBox>("front_input").IsChecked == true;
        control<Slider>("layer_slider").ValueChanged += (_, _) =>
        {
            if (updating_layer) return;
            control<TextBox>("layer_input").Text = ((int)control<Slider>("layer_slider").Value).ToString(CultureInfo.CurrentCulture);
        };
        control<Slider>("layer_slider").AddHandler(PointerPressedEvent, (_, _) =>
        {
            layer_dragging = true;
            settle_timer.Stop();
        }, RoutingStrategies.Bubble, true);
        control<Slider>("layer_slider").AddHandler(PointerReleasedEvent, (_, _) =>
        {
            layer_dragging = false;
            request_layer_preview();
        }, RoutingStrategies.Bubble, true);
        control<Slider>("layer_slider").PointerCaptureLost += (_, _) =>
        {
            layer_dragging = false;
            request_layer_preview();
        };
        control<TextBox>("layer_input").TextChanged += (_, _) =>
        {
            if (updating_layer || control<TextBox>("layer_input").Text == layer_display) return;
            bool valid = layer_value(out int value);
            control<TextBox>("layer_input").Classes.Set("invalid", !valid);
            if (!valid) return;
            layer_display = control<TextBox>("layer_input").Text ?? "";
            control<Slider>("layer_slider").Value = value;
            if (!queue.busy && session.room?.can_edit == true) canvas.set_layer(value);
        };
        number_input("step_input", 1, 64, 1);
        number_input("width_input", 1, 10000, 300);
        number_input("angle_input", -360, 360, 0);
        number_input("count_input", 1, 20000, 50);
        control<TextBox>("svg_input").TextChanged += (_, _) => schedule_preview();
        control<ComboBox>("furniture_input").SelectionChanged += (_, _) => schedule_preview();
        control<CheckBox>("all_items").IsCheckedChanged += (_, _) =>
        {
            if (switching || canvas.active_drawing is not { } item) return;
            item.use_all = control<CheckBox>("all_items").IsChecked == true;
            if (!item.use_all)
            {
                numbers["count_input"] = item.requested_count;
                control<TextBox>("count_input").Text = item.requested_count.ToString(CultureInfo.CurrentCulture);
            }
            update_preview(false);
        };
        var fonts = FontManager.Current.SystemFonts.Select(f => f.Name).Order().ToArray();
        control<ComboBox>("font_input").ItemsSource = fonts;
        control<ComboBox>("font_input").SelectedItem = fonts.FirstOrDefault(x => x == "Segoe UI") ?? fonts.FirstOrDefault();
        control<ComboBox>("font_input").SelectionChanged += (_, _) => schedule_preview();
        control<ComboBox>("shape_input").SelectedIndex = 0;
        control<ComboBox>("shape_input").SelectionChanged += (_, _) =>
        {
            if (switching) return;
            canvas.drawing = tool == "draw" && control<ComboBox>("shape_input").SelectedIndex == 0;
            schedule_preview();
            update_regions();
        };
        setup_grid();
        setup_text_editor();
        KeyDown += keys;
        LayoutUpdated += (_, _) => update_regions();
        PositionChanged += (_, _) => guide?.sync(this, canvas.overlay);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) guide?.sync(this, canvas.overlay);
        };
        Opened += (_, _) => { refresh(); canvas.center_view(); };
        Closing += async (_, e) =>
        {
            if (!queue.busy || closing) return;
            e.Cancel = true;
            queue.cancel();
            show_status("Stopping pending moves");
            while (queue.busy) await Task.Delay(20);
            closing = true;
            Close();
        };
        Closed += (_, _) =>
        {
            closed = true;
            preview_timer.Stop();
            settle_timer.Stop();
            progress_reset.Stop();
            lifetime.Cancel();
            lifetime.Dispose();
            overlay_pointer?.Dispose();
            guide?.Close();
            try { session.restore_preview(); } catch (InvalidOperationException) { }
            session.changed -= session_changed;
            session.item_changed -= grid_capture_item;
            session.start_picked -= grid_start_picked;
            session.pick_start = false;
            queue.progress -= report;
            queue.started -= movement_started;
            queue.item_confirmed -= movement_confirmed;
            movement_reset.Stop();
            move_timer.Stop();
            queue.Dispose();
            input_region.Dispose();
        };
    }

    void number_input(string name, double minimum, double maximum, double initial)
    {
        numbers[name] = initial;
        var input = control<TextBox>(name);
        ToolTip.SetTip(input, $"{minimum} to {maximum}");
        input.TextChanged += (_, _) =>
        {
            if (name == "count_input" && canvas.active_drawing?.use_all == true)
            {
                input.Classes.Set("invalid", false);
                return;
            }
            if (input.Text == numbers[name].ToString("0.##", CultureInfo.CurrentCulture))
            {
                input.Classes.Set("invalid", false);
                return;
            }
            bool valid = double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) &&
                double.IsFinite(value) && value >= minimum && value <= maximum &&
                (name is not ("count_input" or "step_input") || value == Math.Truncate(value));
            input.Classes.Set("invalid", !valid);
            if (!valid) return;
            numbers[name] = value;
            if (name == "step_input") canvas.step = (int)value;
            else schedule_preview();
        };
        input.LostFocus += (_, _) =>
        {
            if (input.Classes.Contains("invalid")) input.Text = numbers[name].ToString(CultureInfo.CurrentCulture);
        };
    }

    void toggle_settings()
    {
        var anchor = canvas.PointToScreen(canvas.screen(default));
        var panel = control<Border>("tool_panel");
        panel.IsVisible = !panel.IsVisible;
        control<Border>("collapsed_edge").IsVisible = !panel.IsVisible;
        control<Grid>("workspace").ColumnDefinitions[0].Width = new GridLength(panel.IsVisible ? 244 : 7);
        var bottom = control<Border>("bottom_edge");
        Grid.SetColumn(bottom, panel.IsVisible ? 1 : 0);
        Grid.SetColumnSpan(bottom, panel.IsVisible ? 2 : 3);
        UpdateLayout();
        var current = canvas.PointToScreen(canvas.screen(default));
        canvas.center += new Vector(current.X - anchor.X, current.Y - anchor.Y) / (RenderScaling * canvas.zoom);
        canvas.redraw();
    }

    void maximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void request_preview()
    {
        if (closed) return;
        client_dirty = true;
        if (!preview_timer.IsEnabled) preview_timer.Start();
        settle_timer.Stop();
        settle_timer.Start();
    }

    void request_layer_preview()
    {
        if (closed) return;
        client_dirty = true;
        layer_waiting = true;
        settle_timer.Stop();
        settle_timer.Start();
    }

    void client_preview()
    {
        if (!client_dirty || queue.busy && !queue.paused) return;
        client_dirty = false;
        try
        {
            bool drawing_preview = canvas.selected_drawings.Count > 0;
            if (!canvas.item_preview && !drawing_preview && !grid_preview &&
                (layer_waiting || !canvas.has_layer_preview))
            { if (!layer_waiting) session.restore_preview(); return; }
            var moves = (canvas.item_preview ? canvas.preview_moves : grid_preview ? canvas.grid_moves
                : drawing_preview ? canvas.preview_moves.Concat(layer_waiting ? [] : canvas.layer_moves).DistinctBy(move => move.before.id)
                : canvas.layer_moves).Take(WallSession.preview_limit + 1).ToArray();
            if (moves.Length == 0 || moves.Length > WallSession.preview_limit) session.restore_preview(); else session.preview(moves);
        }
        catch (Exception error) { show_status(error.Message); }
    }

    void bind(string name, Action action) => control<Button>(name).Click += (_, _) => action();

    void session_changed()
    {
        if (Interlocked.Exchange(ref refresh_pending, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref refresh_pending, 0);
            if (!closed) refresh();
        });
    }

    void refresh()
    {
        var room = session.room;
        if (room_generation != (room?.generation ?? -1))
        {
            room_generation = room?.generation ?? -1;
            move_timer.Stop();
            scheduled_moves.Clear();
            revised_moves = null;
            move_paused = false;
        }
        canvas.refresh();
        control<TextBlock>("room_title").Text = room is null ? session.status : $"{room.name} / {room.items.Count} wall items";
        if (!queue.busy) show_status(session.status);
        var choices = room?.items.GroupBy(x => (x.kind, x.name))
            .Select(g => new Furniture(g.Key.kind, g.Key.name, g.Count())).OrderBy(x => x.name).ToArray() ?? [];
        string signature = string.Join("|", choices.Select(x => $"{x.kind}:{x.name}:{x.count}"));
        if (signature != furniture_signature)
        {
            furniture_signature = signature;
            var input = control<ComboBox>("furniture_input");
            int? selected = (input.SelectedItem as Furniture)?.kind;
            input.ItemsSource = choices;
            input.SelectedItem = choices.FirstOrDefault(x => x.kind == selected);
            var selection = control<ComboBox>("selection_type");
            int? selected_kind = (selection.SelectedItem as Furniture)?.kind;
            selection.ItemsSource = choices;
            selection.SelectedItem = choices.FirstOrDefault(x => x.kind == selected_kind);
            layout_scene();
            refresh_grid_sources(choices);
        }
        reset_grid_room();
        selection_changed();
        if (queue.paused)
        {
            client_dirty = true;
            client_preview();
        }
    }

    void selection_changed()
    {
        control<TextBlock>("selected_count").Text = canvas.selected_drawings.Count > 0
            ? $"{canvas.selected_drawings.Count} {(canvas.selected_drawings.Count == 1 ? "object" : "objects")}"
            : $"{canvas.selected.Count} {(canvas.selected.Count == 1 ? "item" : "items")}";
        control<ToggleButton>("zoom_button").IsChecked = canvas.zoom == .5;
        if (!queue.busy)
        {
            control<Button>("undo_button").IsEnabled = scheduled_moves.Count == 0 && queue.can_undo;
            control<Button>("redo_button").IsEnabled = scheduled_moves.Count == 0 && queue.can_redo;
        }
        canvas.locked = queue.busy && !queue.can_revise;
        bool writable = session.room?.can_edit == true;
        var selection = control<ComboBox>("selection_type");
        var selected_kinds = canvas.chosen.Select(x => x.kind).Distinct().Take(2).ToArray();
        if (selected_kinds.Length == 1 && (selection.SelectedItem as Furniture)?.kind != selected_kinds[0])
            selection.SelectedItem = selection.Items.OfType<Furniture>().FirstOrDefault(x => x.kind == selected_kinds[0]);
        control<Button>("select_type").IsEnabled = !queue.busy && selection.SelectedItem is Furniture;
        var count = control<TextBox>("count_input");
        count.IsEnabled = canvas.active_drawing?.use_all != true;
        if (canvas.active_drawing is { use_all: true } drawing)
            count.Text = drawing.items.Length.ToString(CultureInfo.CurrentCulture);
        refresh_layer(writable);
        foreach (string name in new[] { "nudge_left", "nudge_right", "nudge_up", "nudge_down" })
            control<Button>(name).IsEnabled = writable && (canvas.selected.Count > 0 || canvas.selected_drawings.Count > 0);
        control<Button>("apply_button").IsEnabled = queue.busy || scheduled_moves.Count > 0 || writable && canvas.drawings.Count > 0 &&
            canvas.drawings.All(x => x.error is null && x.placement_error is null && x.points.Length > 0);
        control<StackPanel>("object_actions").IsVisible = canvas.drawings.Count > 0;
        control<StackPanel>("objects_panel").IsVisible = canvas.drawings.Count > 0;
        control<StackPanel>("pattern_editor").IsEnabled = canvas.active_drawing is not null;
        control<Button>("duplicate_drawing").IsEnabled = writable && canvas.active_drawing is { } active && canvas.drawings.Contains(active);
        control<Button>("clear_preview").IsEnabled = writable && canvas.active_drawing is not null;
    }

    void panels()
    {
        foreach (string name in new[] { "select", "text", "svg", "draw" })
            control<ToggleButton>(name + "_tool").IsChecked = name == tool;
        control<StackPanel>("selection_panel").IsVisible = tool == "select";
        control<StackPanel>("pattern_panel").IsVisible = tool != "select";
        control<StackPanel>("text_panel").IsVisible = tool == "text";
        control<StackPanel>("svg_panel").IsVisible = tool == "svg";
        control<StackPanel>("draw_panel").IsVisible = tool == "draw";
        control<CheckBox>("front_input").IsVisible = tool != "select";
        control<Button>("new_drawing").Content = tool == "text" ? "New text" : tool == "svg" ? "New SVG" : "New drawing";
    }

    void choose_tool(string selected)
    {
        if (switching) return;
        end_text_edit(false);
        ++preview_revision;
        tool = selected;
        canvas.selecting_items = selected == "select";
        if (tool == "select")
        {
            canvas.drawing = false;
            canvas.select_drawing(null);
            panels();
        }
        else if (canvas.active_drawing?.tool != tool) new_drawing(tool);
        else
        {
            panels();
            canvas.drawing = tool == "draw" && canvas.active_drawing.shape == "Freehand";
        }
        update_regions();
    }

    WallDrawing new_drawing(string kind, bool generate = true)
    {
        var item = canvas.draft?.tool == kind ? canvas.draft : new WallDrawing(++drawing_number, kind)
        {
            kind = (control<ComboBox>("furniture_input").SelectedItem as Furniture)?.kind ?? 0,
            font = control<ComboBox>("font_input").SelectedItem as string ?? "Segoe UI",
            requested_count = (int)numbers["count_input"],
            origin = canvas.drawings.LastOrDefault()?.origin + new Vector(24, 24) ?? canvas.center
        };
        canvas.selecting_items = false;
        canvas.begin_drawing(item);
        if (generate) update_preview(false);
        canvas.drawing = kind == "draw" && item.shape == "Freehand";
        return item;
    }

    void complete_drawing(WallDrawing item)
    {
        if (!ReferenceEquals(canvas.draft, item) || item.error is not null || !item.contours.Any(path => path.Length > 1)) return;
        bool previous = switching;
        switching = true;
        try { canvas.complete_drawing(); }
        finally { switching = previous; }
        show_drawing(item);
    }

    void duplicate_drawing()
    {
        if (queue.busy) return;
        if (canvas.selected_drawings.Count == 0 || canvas.selected_drawings.Any(item => !canvas.drawings.Contains(item))) return;
        update_preview(false);
        var copies = canvas.selected_drawings.Select(original => new WallDrawing(++drawing_number, original.tool)
        {
            kind = original.kind, text = original.text, font = original.font, svg = original.svg,
            shape = original.shape, width = original.width, rotation = original.rotation,
            requested_count = original.requested_count, use_all = original.use_all, contours = original.contours,
            origin = original.origin + new Vector(24, 24), error = original.error
        }).ToArray();
        foreach (var copy in copies) canvas.drawings.Add(copy);
        canvas.select_drawings(copies);
        layout_scene();
    }

    void show_drawing(WallDrawing? item)
    {
        if (switching) return;
        switching = true;
        ++preview_revision;
        var selection = control<ListBox>("objects_list").SelectedItems;
        selection?.Clear();
        foreach (var selected_item in canvas.selected_drawings.Where(canvas.drawings.Contains)) selection?.Add(selected_item);
        if (item is not null)
        {
            control<CheckBox>("all_items").IsChecked = item.use_all;
            tool = item.tool;
            canvas.selecting_items = false;
            panels();
            control<TextBox>("svg_input").Text = item.svg;
            control<ComboBox>("font_input").SelectedItem = item.font;
            var furniture = control<ComboBox>("furniture_input");
            furniture.SelectedItem = furniture.Items.OfType<Furniture>().FirstOrDefault(x => x.kind == item.kind);
            var shapes = control<ComboBox>("shape_input");
            shapes.SelectedItem = shapes.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Content?.ToString() == item.shape);
            foreach (var value in new[] { ("width_input", item.width), ("angle_input", item.rotation), ("count_input", (double)item.requested_count) })
            {
                numbers[value.Item1] = value.Item2;
                control<TextBox>(value.Item1).Text = value.Item2.ToString("0.##", CultureInfo.CurrentCulture);
            }
            canvas.drawing = false;
        }
        switching = false;
        selection_changed();
        request_preview();
    }

    void layout_scene()
    {
        if (session.room is not { } room) return;
        WallDrawing.layout(canvas.drawings, room, canvas.active_drawing);
        canvas.drawings_changed();
        var item = canvas.active_drawing;
        if (item is not null)
            control<TextBlock>("available_count").Text = item.error ?? item.placement_error ??
                $"{item.items.Length} items assigned to this object." + (item.assignment_note is { } note ? $" {note}" : "");
        selection_changed();
    }

    void schedule_preview()
    {
        if (closed || switching || tool == "select" || canvas.active_drawing is null) return;
        int revision = ++preview_revision;
        _ = update_preview_later(revision, lifetime.Token);
    }

    async Task update_preview_later(int revision, CancellationToken token)
    {
        try
        {
            await Task.Delay(120, token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (!closed && revision == preview_revision && tool != "select") update_preview(false);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    void update_preview(bool report_errors)
    {
        if (canvas.active_drawing is not { } item) return;
        try
        {
            item.kind = (control<ComboBox>("furniture_input").SelectedItem as Furniture)?.kind ?? item.kind;
            if (!item.use_all) item.requested_count = (int)numbers["count_input"];
            item.width = numbers["width_input"];
            item.rotation = numbers["angle_input"];
            item.svg = control<TextBox>("svg_input").Text ?? "";
            item.font = control<ComboBox>("font_input").SelectedItem as string ?? "Segoe UI";
            item.shape = (control<ComboBox>("shape_input").SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Freehand";
            item.contours = item.tool switch
            {
                "text" => PatternBuilder.text(item.text, item.font),
                "svg" => PatternBuilder.svg(item.svg),
                "draw" when item.shape != "Freehand" => PatternBuilder.shape(item.shape),
                _ => item.contours
            };
            item.error = null;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or System.Xml.XmlException or InvalidOperationException)
        {
            item.error = error.Message;
            item.contours = [];
            if (report_errors) show_status(error.Message);
        }
        item.refresh_label();
        complete_drawing(item);
        layout_scene();
    }

    async Task choose_svg()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open SVG", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("SVG") { Patterns = ["*.svg"] }]
            });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            if (stream.Length > 4_000_000) throw new InvalidOperationException("The SVG is too large.");
            using var reader = new StreamReader(stream);
            control<TextBox>("svg_input").Text = await reader.ReadToEndAsync();
        }
        catch (Exception error) { show_status(error.Message); }
    }

    void nudge(int x, int y)
    {
        if (session.room?.can_edit != true || canvas.selected.Count == 0 && canvas.selected_drawings.Count == 0) return;
        if (queue.busy && !queue.can_revise) return;
        if (queue.busy) move_paused = queue.pause() || move_paused;
        double unit = session.room is { } current ? 64.0 / current.plan.Scale : 1;
        var delta = new Vector(x * canvas.step * unit, y * canvas.step * unit);
        if (canvas.selected_drawings.Count > 0)
        {
            canvas.move_selected(delta);
            if (move_paused || scheduled_moves.Count > 0) schedule_moves(canvas.selected_moves());
        }
        else schedule_moves(canvas.transform((p, _) => p + delta));
    }

    void schedule_moves(IReadOnlyList<WallMove> moves)
    {
        foreach (var move in moves)
            scheduled_moves[move.before.id] = scheduled_moves.TryGetValue(move.before.id, out var previous)
                ? previous with { after = move.after } : move;
        if (scheduled_moves.Count == 0) return;
        movement_reset.Stop();
        progress_reset.Stop();
        canvas.revise_positions(moves);
        client_dirty = true;
        client_preview();
        move_timer.Stop();
        move_timer.Start();
        control<TextBlock>("movement_status").Text = move_paused ? "Paused" : "Waiting";
        selection_changed();
    }

    void flush_moves()
    {
        move_timer.Stop();
        if (canvas.interacting || scheduled_moves.Count == 0) return;
        if (queue.busy && !queue.can_revise)
        {
            move_timer.Start();
            return;
        }
        var moves = scheduled_moves.Values.ToArray();
        scheduled_moves.Clear();
        if (move_paused)
        {
            move_paused = false;
            queue.resume(moves);
        }
        else if (queue.busy)
        {
            queue.pause();
            queue.resume(moves);
        }
        else _ = run(() => queue.apply(moves));
    }

    void cancel_moves()
    {
        move_timer.Stop();
        scheduled_moves.Clear();
        move_paused = false;
        revised_moves = null;
        queue.cancel();
        canvas.finish_move();
        session.restore_preview();
        control<TextBlock>("movement_status").Text = "";
    }

    bool layer_value(out int value) => int.TryParse(control<TextBox>("layer_input").Text, out value) &&
        value >= 0 && value <= (session.room?.plan.Length ?? 0);

    void refresh_layer(bool writable)
    {
        var items = canvas.selected_drawings.Count == 0 ? canvas.chosen : [];
        var panel = control<StackPanel>("layer_panel");
        panel.IsVisible = items.Count > 0;
        panel.IsEnabled = writable;
        string selection = string.Join(";", items.Select(item => $"{item.id}:{canvas.layer(item)}"));
        if (selection != layer_selection)
        {
            layer_selection = selection;
            updating_layer = true;
            var slider = control<Slider>("layer_slider");
            slider.Maximum = session.room?.plan.Length ?? 0;
            slider.Value = items.Count > 0 ? Math.Clamp(canvas.layer(items[0]), 0, slider.Maximum) : 0;
            var input = control<TextBox>("layer_input");
            bool mixed = items.Select(canvas.layer).Distinct().Skip(1).Any();
            layer_display = mixed ? "" : ((int)slider.Value).ToString(CultureInfo.CurrentCulture);
            input.Text = layer_display;
            input.PlaceholderText = mixed ? "Mixed" : "";
            input.Classes.Remove("invalid");
            updating_layer = false;
        }
    }

    async Task run(Func<Task> action)
    {
        if (queue.busy) return;
        try { await action(); }
        catch (Exception error) { show_status(error.Message); }
        finally
        {
            canvas.finish_move();
            try { session.restore_preview(); }
            catch (InvalidOperationException error) { show_status(error.Message); }
            client_dirty = false;
            selection_changed();
            if (canvas.has_layer_preview) request_preview();
        }
    }

    public void show_status(string text)
    {
        var status = control<TextBlock>("status_text");
        status.Text = text;
        status.IsVisible = text is not ("Connected" or "Waiting for G-Earth" or "Enter or reload a room") && text.Length > 0;
    }

    void movement_started(IReadOnlyList<WallMove> moves) => Dispatcher.UIThread.Post(() =>
    {
        if (closed) return;
        movement_reset.Stop();
        if (!queue.paused) canvas.present(moves);
        canvas.begin_movement(moves);
    });

    void movement_confirmed(WallMove move) => Dispatcher.UIThread.Post(() =>
    {
        if (!closed) canvas.confirm_movement(move);
    });

    void report(string text, int count, int total)
    {
        bool running = queue.busy;
        Dispatcher.UIThread.Post(() =>
        {
            if (closed) return;
            if (!running && text.Length > 0 && text != "Stopped") show_status(text);
            var fill = control<Border>("move_fill");
            var scale = (ScaleTransform)fill.RenderTransform!;
            progress_reset.Stop();
            if (count == 0) scale.Transitions = null;
            fill.Opacity = 1;
            scale.ScaleX = total > 0 ? Math.Clamp((double)count / total, 0, 1) : 0;
            scale.Transitions = fill_transitions;
            ToolTip.SetTip(control<Button>("apply_button"), running ? $"{count} / {total}. Click to stop." : "Move objects");
            canvas.movement_text = queue.paused ? "Paused" : scheduled_moves.Count > 0 ? "Waiting" :
                total == 0 ? "" : $"{(running ? "Moving" : "Moved")} {canvas.moved_count} / {total}";
            control<TextBlock>("movement_status").Text = canvas.movement_text;
            canvas.movement_progress = total > 0 ? (double)canvas.moved_count / total : 0;
            canvas.redraw();
            if (!running) { progress_reset.Start(); movement_reset.Start(); }
            selection_changed();
        });
    }

    void update_overlay()
    {
        canvas.overlay = control<ToggleButton>("clickthrough_button").IsChecked == true;
        if (canvas.overlay && OverlayRegion.available) guide ??= new OverlayGuide(canvas, control<Border>("collapsed_edge"), control<Border>("bottom_edge"), error => { control<ToggleButton>("clickthrough_button").IsChecked = false; show_status(error.Message); });
        var edge_brush = canvas.overlay ? Brush.Parse("#40AAB7AF") : (IBrush)this.FindResource("ChromeBrush")!;
        foreach (string edge in new[] { "collapsed_edge", "resize_edge", "bottom_edge" })
            control<Border>(edge).Background = edge_brush;
        control<ToggleButton>("capture_button").IsVisible = canvas.overlay;
        canvas.redraw();
    }

    void update_regions()
    {
        if (closed) return;
        try
        {
            var toolbar = control<Control>("canvas_toolbar");
            var exclusions = new List<PixelRect>();
            if (toolbar.IsVisible)
                exclusions.Add(new PixelRect(toolbar.PointToScreen(default), PixelSize.FromSize(toolbar.Bounds.Size, RenderScaling)));
            if (grid_window is { IsVisible: true } window)
                exclusions.Add(new PixelRect(window.Position, PixelSize.FromSize(window.FrameSize ?? window.ClientSize, window.RenderScaling)));
            canvas.obstruction = toolbar.TranslatePoint(default, canvas) is { } toolbar_origin
                ? new Rect(toolbar_origin, toolbar.Bounds.Size) : default;
            if (canvas.overlay && IsVisible && WindowState != WindowState.Minimized)
                overlay_pointer?.update(new PixelRect(canvas.PointToScreen(default), PixelSize.FromSize(canvas.Bounds.Size, RenderScaling)), exclusions);
            else overlay_pointer?.disable();
            guide?.sync(this, canvas.overlay);
            if (canvas.interacting && canvas.overlay) return;
            bool full = canvas.capture || canvas.drawing || canvas.pick_grid ||
                overlay_pointer?.modifiers is { } modifiers && modifiers != KeyModifiers.None;
            if (!canvas.overlay || full) { input_region.update(false, []); return; }
            var areas = new List<Rect>();
            foreach (string name in new[] { "titlebar", "tool_panel", "canvas_toolbar", "resize_edge" })
            {
                var item = control<Control>(name);
                if (!item.IsVisible) continue;
                if (item.TranslatePoint(default, this) is { } p) areas.Add(new Rect(p, item.Bounds.Size));
            }
            if (canvas.TranslatePoint(default, this) is { } origin)
            {
                areas.AddRange(canvas.hit_regions().Select(r => r.Translate((Vector)origin)));
                if (edited_text is not null) areas.Add(editor_area.Translate((Vector)origin));
            }
            input_region.update(true, areas);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            control<ToggleButton>("clickthrough_button").IsChecked = false;
            show_status(error.Message);
        }
    }

    void keys(object? sender, KeyEventArgs e)
    {
        if (e.Source is Control source && (source is TextBox or Slider ||
            source.GetVisualAncestors().Any(x => x is TextBox or Slider))) return;
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (control && e.Key == Key.D0) canvas.actual_size();
        else if (control && e.Key == Key.A)
        {
            if (canvas.selecting_items)
            {
                if (control<ComboBox>("selection_type").SelectedItem is Furniture choice) canvas.select_all(choice.kind);
                else canvas.select_all();
            }
            else if (canvas.drawings.Count > 0) canvas.select_drawings(canvas.drawings);
        }
        else if (control && e.Key == Key.Z) _ = run(queue.undo);
        else if (control && e.Key == Key.Y) _ = run(queue.redo);
        else if (e.Key == Key.Escape)
        {
            cancel_moves();
            canvas.cancel_edit();
            control<ToggleButton>("capture_button").IsChecked = false;
        }
        else if (e.Key is Key.Delete or Key.Back && canvas.active_drawing is not null)
        {
            canvas.clear_pattern();
            layout_scene();
        }
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            nudge(e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0,
                e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0);
        else return;
        e.Handled = true;
    }
}
