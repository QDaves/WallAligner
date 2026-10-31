using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using WallAligner.Core;

namespace WallAligner;

public partial class MainWindow
{
    WallDrawing? edited_text;
    string original_text = "";
    double original_width;
    Point original_origin;
    Point text_anchor;
    double text_scale;
    bool updating_editor;
    Rect editor_area;
    readonly RotateTransform text_rotation = new();

    void setup_text_editor()
    {
        var editor = control<TextBox>("text_editor");
        editor.RenderTransform = text_rotation;
        bool on_border(Point point) => point.X < 7 || point.Y < 7 || point.X > editor.Bounds.Width - 7 || point.Y > editor.Bounds.Height - 7;
        editor.AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (edited_text is not { } item || !args.GetCurrentPoint(editor).Properties.IsLeftButtonPressed ||
                !(on_border(args.GetPosition(editor)) || args.KeyModifiers.HasFlag(KeyModifiers.Alt))) return;
            var point = args.GetPosition(canvas);
            end_text_edit(false);
            canvas.Focus();
            canvas.drag_drawing(item, point, args.Pointer);
            args.Handled = true;
        }, RoutingStrategies.Tunnel);
        editor.PointerMoved += (_, args) => editor.Cursor = new Cursor(on_border(args.GetPosition(editor))
            ? StandardCursorType.SizeAll : StandardCursorType.Ibeam);
        canvas.text_edit_requested += begin_text_edit;
        canvas.changed += position_text_editor;
        control<ListBox>("objects_list").DoubleTapped += (_, _) =>
        {
            if (canvas.active_drawing is { tool: "text" } item) begin_text_edit(item);
        };
        editor.TextChanged += (_, _) =>
        {
            if (updating_editor || edited_text is not { } item) return;
            item.text = editor.Text ?? "";
            rebuild_text(item);
        };
        editor.LostFocus += (_, _) => end_text_edit(false);
        editor.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                end_text_edit(true);
                canvas.Focus();
                args.Handled = true;
            }
            else if (args.Key == Key.Enter && args.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                end_text_edit(false);
                canvas.Focus();
                args.Handled = true;
            }
        }, RoutingStrategies.Tunnel, true);
    }

    static Rect text_bounds(WallDrawing item)
    {
        var points = item.contours.SelectMany(x => x).ToArray();
        return points.Length == 0 ? default : new Rect(
            new Point(points.Min(x => x.X), points.Min(x => x.Y)),
            new Point(points.Max(x => x.X), points.Max(x => x.Y)));
    }

    static Vector rotate_text(Vector point, double angle)
    {
        double radians = angle * Math.PI / 180;
        return new Vector(point.X * Math.Cos(radians) - point.Y * Math.Sin(radians),
            point.X * Math.Sin(radians) + point.Y * Math.Cos(radians));
    }

    void begin_text_edit(WallDrawing item)
    {
        if (queue.busy || item.tool != "text") return;
        if (edited_text == item) { control<TextBox>("text_editor").Focus(); return; }
        end_text_edit(false);
        canvas.select_drawing(item);
        edited_text = item;
        original_text = item.text;
        original_width = item.width;
        original_origin = item.origin;
        var ink = text_bounds(item);
        text_scale = item.width / Math.Max(1, ink.Width);
        text_anchor = item.origin - rotate_text((Vector)ink.Center * text_scale, item.rotation);
        var editor = control<TextBox>("text_editor");
        updating_editor = true;
        editor.Text = item.text;
        updating_editor = false;
        editor.IsVisible = true;
        canvas.editing_text = item;
        position_text_editor();
        canvas.redraw();
        Dispatcher.UIThread.Post(() =>
        {
            if (edited_text != item) return;
            editor.Focus();
            editor.CaretIndex = editor.Text?.Length ?? 0;
            editor.SelectionStart = editor.SelectionEnd = editor.CaretIndex;
        });
    }

    void end_text_edit(bool cancel)
    {
        if (edited_text is not { } item) return;
        edited_text = null;
        canvas.editing_text = null;
        control<TextBox>("text_editor").IsVisible = false;
        editor_area = default;
        if (cancel)
        {
            item.text = original_text;
            item.width = original_width;
            item.origin = original_origin;
            set_text_width(item.width);
            rebuild_text(item);
        }
        canvas.redraw();
    }

    void set_text_width(double width)
    {
        bool previous = switching;
        switching = true;
        numbers["width_input"] = width;
        control<TextBox>("width_input").Text = width.ToString("0.##", CultureInfo.CurrentCulture);
        switching = previous;
    }

    void rebuild_text(WallDrawing item)
    {
        try
        {
            item.contours = PatternBuilder.text(item.text, item.font);
            item.error = null;
        }
        catch (ArgumentException error)
        {
            item.contours = [];
            item.error = error.Message;
        }
        if (edited_text == item)
        {
            var ink = text_bounds(item);
            item.width = Math.Max(1, ink.Width * text_scale);
            item.origin = text_anchor + rotate_text((Vector)ink.Center * text_scale, item.rotation);
            set_text_width(item.width);
        }
        item.refresh_label();
        layout_scene();
    }

    void position_text_editor()
    {
        if (edited_text is not { } item) return;
        if (!canvas.drawings.Contains(item)) { end_text_edit(false); return; }
        var editor = control<TextBox>("text_editor");
        using var measure = PatternBuilder.text_layout(item.text, item.font);
        double scale = text_scale * canvas.zoom;
        double width = Math.Max(24, measure.WidthIncludingTrailingWhitespace * scale + 14);
        double height = Math.Max(26, measure.Height * scale + 12);
        var top_left = canvas.screen(text_anchor) - rotate_text(new Vector(6, 6), item.rotation);
        var center = top_left + rotate_text(new Vector(width / 2, height / 2), item.rotation);
        editor.FontFamily = new FontFamily(item.font);
        editor.FontSize = Math.Max(1, 100 * scale);
        editor.LineHeight = 120 * scale;
        editor.Width = width;
        editor.Height = height;
        Canvas.SetLeft(editor, center.X - width / 2);
        Canvas.SetTop(editor, center.Y - height / 2);
        text_rotation.Angle = item.rotation;
        double radians = item.rotation * Math.PI / 180;
        double half_x = (Math.Abs(Math.Cos(radians)) * width + Math.Abs(Math.Sin(radians)) * height) / 2;
        double half_y = (Math.Abs(Math.Sin(radians)) * width + Math.Abs(Math.Cos(radians)) * height) / 2;
        editor_area = new Rect(center.X - half_x, center.Y - half_y, half_x * 2, half_y * 2);
    }
}
