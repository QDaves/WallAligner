using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Qx;
using Qx.Model;
using WallAligner.Core;

namespace WallAligner;

public partial class MainWindow
{
    WallGrid grid = new();
    bool grid_loading;
    bool grid_ascending = true;
    long grid_generation = -1;
    long grid_room = -1;
    readonly GridView grid_view = new();
    Window? grid_window;
    bool grid_open => grid_window?.IsVisible == true;
    bool grid_preview => grid_open;
    T grid_control<T>(string name) where T : Control =>
        grid_view.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing grid control: {name}");

    void setup_grid()
    {
        void bind_grid(string name, Action action) => grid_control<Button>(name).Click += (_, _) =>
        {
            try { action(); }
            catch (Exception error) { grid_error(error.Message); }
        };
        session.item_changed += grid_capture_item;
        session.start_picked += grid_start_picked;
        canvas.grid_picked += point =>
        {
            if (session.room is not { } room) return;
            var anchor = grid.start ?? room.items.FirstOrDefault()?.location ?? new WallLocation(0, 0, 0, 0, WallOrientation.Right);
            grid.start = WallLayout.locate(room, point, anchor);
            grid_control<ToggleButton>("grid_pick").IsChecked = false;
            refresh_grid();
        };
        grid_window = new Window
        {
            Title = "Grid alignment", Content = grid_view, Width = 680, Height = 690,
            MinWidth = 590, MinHeight = 570, Topmost = Topmost,
            Background = (IBrush)this.FindResource("PanelBrush")!, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        grid_window.Closing += (_, e) =>
        {
            if (!closed && e.CloseReason == WindowCloseReason.WindowClosing)
            {
                e.Cancel = true;
                grid_window.Hide();
            }
        };
        grid_window.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (!grid_open)
            {
                grid_control<ToggleButton>("grid_pick").IsChecked = false;
                grid_control<ToggleButton>("grid_capture").IsChecked = false;
            }
            refresh_grid();
            update_regions();
        };
        grid_window.PositionChanged += (_, _) => update_regions();
        grid_window.LayoutUpdated += (_, _) => { if (grid_open) update_regions(); };
        grid_window.KeyDown += (_, e) =>
        {
            if (e.Source is Control source && (source is TextBox or ComboBox || source.GetVisualAncestors().Any(x => x is TextBox or ComboBox))) return;
            if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
            shift_grid(e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0,
                e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0);
            e.Handled = true;
        };
        bind("grid_button", () =>
        {
            if (!grid_open) { grid_window.Show(this); grid_window.WindowStartupLocation = WindowStartupLocation.Manual; }
            grid_window.Activate();
            refresh_grid();
        });
        bind_grid("grid_up", () => shift_grid(0, -1));
        bind_grid("grid_down", () => shift_grid(0, 1));
        bind_grid("grid_left", () => shift_grid(-1, 0));
        bind_grid("grid_right", () => shift_grid(1, 0));
        foreach (string name in new[] { "grid_columns", "grid_rows", "grid_spacing_x", "grid_spacing_y", "grid_offset", "grid_layer" })
        {
            grid_control<TextBox>(name).TextChanged += (_, _) => { if (!grid_loading) read_grid(); };
            bind_grid(name + "_less", () => adjust_grid(name, -1));
            bind_grid(name + "_more", () => adjust_grid(name, 1));
        }
        grid_control<TextBox>("grid_ids").TextChanged += (_, _) =>
        {
            if (grid_loading || session.room is not { } room) return;
            try
            {
                grid.assign(WallGrid.parse_ids(grid_control<TextBox>("grid_ids").Text ?? ""), room);
                write_grid();
            }
            catch (Exception error) { grid_error(error.Message); }
        };
        bind_grid("grid_import_type", () =>
        {
            if (session.room is not { } room || grid_control<ComboBox>("grid_furniture").SelectedItem is not Furniture choice) return;
            grid.assign(room.items.Where(x => x.kind == choice.kind).OrderBy(x => x.id).Select(x => x.id), room);
            write_grid();
        });
        bind_grid("grid_from_selection", () =>
        {
            if (session.room is not { } room) return;
            grid.assign(canvas.chosen.Select(x => x.id), room);
            write_grid();
        });
        bind_grid("grid_sort", () =>
        {
            if (session.room is not { } room) return;
            var ids = grid.cells.Select(x => x.id);
            grid.assign(grid_ascending ? ids.Order() : ids.OrderDescending(), room);
            grid_ascending = !grid_ascending;
            write_grid();
        });
        bind_grid("grid_rotate", () => { grid.turn(); refresh_grid(); });
        bind_grid("grid_clear", () => { grid = new(); write_grid(); });
        bind_grid("grid_apply", () => _ = run(async () =>
        {
            if (session.room is not { } room) return;
            grid_control<ToggleButton>("grid_capture").IsChecked = false;
            var moves = grid.moves(room);
            await queue.apply(moves);
        }));
        bind_grid("grid_set_cell", () =>
        {
            if (grid_control<ListBox>("grid_cells").SelectedItem is not GridCell cell) return;
            if (!int.TryParse(grid_control<TextBox>("cell_column").Text, out int column) || column < 0 || column >= grid.columns ||
                !int.TryParse(grid_control<TextBox>("cell_row").Text, out int row) || row < 0 || row >= grid.rows)
            { grid_error("Choose a cell inside the grid."); return; }
            if (!grid.custom_cells)
                grid.cells = grid.cells.Select((x, i) => x with { column = i % grid.columns, row = i / grid.columns }).ToList();
            grid.custom_cells = true;
            grid.cells[grid.cells.FindIndex(x => x.id == cell.id)] = cell with { column = column, row = row };
            refresh_grid();
        });
        grid_control<ListBox>("grid_cells").SelectionChanged += (_, _) =>
        {
            if (grid_control<ListBox>("grid_cells").SelectedItem is not GridCell cell) return;
            grid_control<TextBox>("cell_column").Text = cell.column.ToString();
            grid_control<TextBox>("cell_row").Text = cell.row.ToString();
        };
        grid_control<ComboBox>("grid_groups").SelectionChanged += (_, _) =>
        {
            if (grid_loading || session.room is not { } room || grid_control<ComboBox>("grid_groups").SelectedItem is not GridGroup group) return;
            grid.assign(group.ids, room);
            write_grid();
        };
        grid_control<ToggleButton>("grid_pick").IsCheckedChanged += (_, _) =>
        {
            canvas.pick_grid = grid_control<ToggleButton>("grid_pick").IsChecked == true;
            session.pick_start = canvas.pick_grid;
            canvas.redraw();
        };
        bind_grid("grid_load", () => _ = grid_file(false));
        bind_grid("grid_save", () => _ = grid_file(true));
    }

    void shift_grid(int x, int y)
    {
        if (queue.busy || session.room is not { } room) return;
        if (!int.TryParse(grid_control<TextBox>("grid_step").Text, out int step) || step is < 1 or > 9999)
        { grid_error("Choose a step between 1 and 9999."); return; }
        grid.shift(room, x * step, y * step);
        refresh_grid();
    }

    void adjust_grid(string name, int delta)
    {
        if (queue.busy) return;
        var input = grid_control<TextBox>(name);
        if (!int.TryParse(input.Text, out int value))
        {
            grid_error("Enter a whole number first.");
            return;
        }
        int minimum = name is "grid_offset" or "grid_layer" ? -9999 : 1;
        int maximum = name switch
        {
            "grid_columns" => 20000 / Math.Max(1, grid.rows),
            "grid_rows" => 20000 / Math.Max(1, grid.columns),
            _ => 9999
        };
        input.Text = Math.Clamp((long)value + delta, minimum, maximum).ToString();
        read_grid();
    }

    void reset_grid_room()
    {
        var room = session.room;
        if (grid_generation == (room?.generation ?? -1) && grid_room == (room?.id ?? -1)) return;
        grid_generation = room?.generation ?? -1;
        grid_room = room?.id ?? -1;
        grid = new();
        grid_control<ToggleButton>("grid_capture").IsChecked = false;
        grid_control<ToggleButton>("grid_pick").IsChecked = false;
        write_grid();
    }

    void refresh_grid_sources(Furniture[] choices)
    {
        grid_loading = true;
        try
        {
            var input = grid_control<ComboBox>("grid_furniture");
            int? kind = (input.SelectedItem as Furniture)?.kind;
            input.ItemsSource = choices;
            input.SelectedItem = choices.FirstOrDefault(x => x.kind == kind) ?? choices.FirstOrDefault();
            grid_control<ComboBox>("grid_groups").ItemsSource = session.room is { } room ? WallGrid.groups(room) : [];
        }
        finally { grid_loading = false; }
    }

    void grid_capture_item(WallEntry item) => Dispatcher.UIThread.Post(() =>
    {
        if (closed || queue.busy || grid_control<ToggleButton>("grid_capture").IsChecked != true || session.room is not { } room || grid.cells.Any(x => x.id == item.id)) return;
        grid.assign(grid.cells.Select(x => x.id).Append(item.id), room);
        write_grid();
    });

    void grid_start_picked(WallLocation location) => Dispatcher.UIThread.Post(() =>
    {
        if (closed) return;
        grid.start = location;
        grid_control<ToggleButton>("grid_pick").IsChecked = false;
        refresh_grid();
    });

    void read_grid()
    {
        var values = new Dictionary<string, int>();
        foreach (string name in new[] { "grid_columns", "grid_rows", "grid_spacing_x", "grid_spacing_y", "grid_offset", "grid_layer" })
        {
            var input = grid_control<TextBox>(name);
            bool valid = int.TryParse(input.Text, out int value);
            input.Classes.Set("invalid", !valid);
            if (!valid) { grid_error("Enter whole numbers for the grid settings."); return; }
            values[name] = value;
        }
        if (values["grid_columns"] < 1 || values["grid_rows"] < 1 ||
            (long)values["grid_columns"] * values["grid_rows"] > 20000 ||
            values["grid_spacing_x"] is < 1 or > 9999 || values["grid_spacing_y"] is < 1 or > 9999 ||
            Math.Abs((long)values["grid_offset"]) > 9999 || Math.Abs((long)values["grid_layer"]) > 9999)
        { grid_error("Check the grid dimensions, spacing and layer."); return; }
        if (grid.columns == values["grid_columns"] && grid.rows == values["grid_rows"] &&
            grid.spacing_x == values["grid_spacing_x"] && grid.spacing_y == values["grid_spacing_y"] &&
            grid.slope == values["grid_offset"] && grid.layer == values["grid_layer"]) return;
        grid.columns = values["grid_columns"];
        grid.rows = values["grid_rows"];
        grid.spacing_x = values["grid_spacing_x"];
        grid.spacing_y = values["grid_spacing_y"];
        grid.slope = values["grid_offset"];
        grid.layer = values["grid_layer"];
        refresh_grid();
    }

    void write_grid()
    {
        grid_loading = true;
        try
        {
            foreach (var entry in new[] { ("grid_columns", grid.columns), ("grid_rows", grid.rows), ("grid_spacing_x", grid.spacing_x),
                ("grid_spacing_y", grid.spacing_y), ("grid_offset", grid.slope), ("grid_layer", grid.layer) })
                grid_control<TextBox>(entry.Item1).Text = entry.Item2.ToString();
            grid_control<TextBox>("grid_ids").Text = string.Join(Environment.NewLine, grid.cells.Select(x => x.id));
        }
        finally { grid_loading = false; }
        refresh_grid();
    }

    void refresh_grid()
    {
        try
        {
            var room = session.room;
            var moves = room is null ? [] : grid.moves(room);
            canvas.grid_moves = grid_open ? moves : [];
            var cells = grid.cells.Select((x, i) => grid.custom_cells ? x : x with { column = i % grid.columns, row = i / grid.columns }).ToArray();
            var list = grid_control<ListBox>("grid_cells");
            if (list.ItemsSource is not GridCell[] previous || !previous.SequenceEqual(cells))
            {
                var selected = (list.SelectedItem as GridCell)?.id;
                list.ItemsSource = cells;
                list.SelectedItem = cells.FirstOrDefault(x => x.id == selected);
            }
            grid_control<TextBlock>("grid_info").Text = moves.Count == 0 ? "Add furniture or use the current selection." : $"{moves.Count} items in this grid.";
            grid_control<Button>("grid_apply").IsEnabled = room?.can_edit == true && !queue.busy && moves.Count > 0;
            request_preview();
            canvas.redraw();
        }
        catch (Exception error) { grid_error(error.Message); }
    }

    void grid_error(string message)
    {
        canvas.grid_moves = [];
        canvas.redraw();
        grid_control<TextBlock>("grid_info").Text = message;
        grid_control<Button>("grid_apply").IsEnabled = false;
    }

    async Task grid_file(bool save)
    {
        try
        {
            if (session.room is not { } room) return;
            if (save)
            {
                var content = grid.save(room);
                var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Save grid", SuggestedFileName = "grid.txt", DefaultExtension = "txt" });
                if (file is null) return;
                await using var stream = await file.OpenWriteAsync();
                stream.SetLength(0);
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(content);
            }
            else
            {
                var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Load grid", AllowMultiple = false });
                if (files.Count == 0) return;
                await using var stream = await files[0].OpenReadAsync();
                if (stream.Length > 4_000_000) throw new ArgumentException("The grid file is too large.");
                using var reader = new StreamReader(stream);
                var loaded = WallGrid.load(await reader.ReadToEndAsync(), room);
                if (session.room?.generation != room.generation) throw new InvalidOperationException("The room changed while loading the grid.");
                grid = loaded;
                write_grid();
            }
        }
        catch (Exception error) { grid_error(error.Message); }
    }
}
