using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace WallAligner;

public sealed class OverlayGuide : IDisposable
{
    readonly WallCanvas canvas;
    readonly Control edge;
    readonly Control bottom;
    readonly Guide drawing;
    readonly DispatcherTimer timer;
    readonly Action<Exception> failed;
    Window? owner;
    nint handle;
    nint memory;
    nint pixels;
    nint dib;
    nint previous_bitmap;
    RenderTargetBitmap? bitmap;
    PixelSize pixel_size;
    double scaling;
    bool dirty;
    bool disposed;
    long revision = -1;
    PixelPoint screen_origin;
    const uint overlay_style = 0x080800A0;
    const uint popup_style = 0x80000000;

    sealed class Guide(WallCanvas canvas) : Control
    {
        public Point offset;
        public Rect edge;
        public Rect bottom;
        static readonly IBrush edge_brush = Brush.Parse("#40AAB7AF");
        Rect previous_bounds;
        Rect previous_obstruction;
        Geometry? visible;

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(canvas.Bounds.Size);
            if (visible is null || bounds != previous_bounds || canvas.obstruction != previous_obstruction)
            {
                previous_bounds = bounds;
                previous_obstruction = canvas.obstruction;
                visible = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(bounds), new RectangleGeometry(canvas.obstruction));
            }
            using (context.PushTransform(Matrix.CreateTranslation(offset.X, offset.Y)))
            using (context.PushGeometryClip(visible)) canvas.render_scene(context);
            context.DrawRectangle(edge_brush, null, bottom);
            if (edge.Width > 0) context.DrawRectangle(edge_brush, null, edge);
        }
    }

    public OverlayGuide(WallCanvas canvas, Control edge, Control bottom, Action<Exception> failed)
    {
        this.canvas = canvas;
        this.edge = edge;
        this.bottom = bottom;
        this.failed = failed;
        drawing = new Guide(canvas);
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) =>
        {
            if (!dirty || owner is null) return;
            try { present(); }
            catch (Exception error) { hide(); failed(error); }
        };
    }

    public void sync(Window owner, bool enabled)
    {
        if (disposed) return;
        this.owner = owner;
        if (!enabled || !owner.IsVisible || owner.WindowState == WindowState.Minimized)
        {
            hide();
            return;
        }
        if (owner.ClientSize.Width < 1 || owner.ClientSize.Height < 1) return;
        if (handle == 0)
        {
            var parent = owner.TryGetPlatformHandle()?.Handle ?? throw new InvalidOperationException("The overlay window is not ready.");
            handle = CreateWindowExW(overlay_style, "STATIC", "WallAligner overlay", popup_style,
                0, 0, 1, 1, parent, 0, 0, 0);
            if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var origin = owner.PointToScreen(default);
        if (screen_origin != origin)
        {
            if (!SetWindowPos(handle, 0, origin.X, origin.Y, 0, 0, 0x15))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            screen_origin = origin;
        }
        var offset = canvas.TranslatePoint(default, owner) ?? default;
        var edge_bounds = edge.IsVisible && edge.TranslatePoint(default, owner) is { } location
            ? new Rect(location, edge.Bounds.Size) : default;
        var bottom_bounds = bottom.TranslatePoint(default, owner) is { } bottom_origin
            ? new Rect(bottom_origin, bottom.Bounds.Size) : default;
        dirty |= revision != canvas.revision || drawing.Bounds.Size != owner.ClientSize || drawing.offset != offset || drawing.edge != edge_bounds || drawing.bottom != bottom_bounds;
        revision = canvas.revision;
        drawing.offset = offset;
        drawing.edge = edge_bounds;
        drawing.bottom = bottom_bounds;
        drawing.Measure(owner.ClientSize);
        drawing.Arrange(new Rect(owner.ClientSize));
        drawing.InvalidateVisual();
        if (!timer.IsEnabled)
        {
            present();
            ShowWindow(handle, 4);
            timer.Start();
        }
    }

    void present()
    {
        if (owner is null || handle == 0) return;
        var size = PixelSize.FromSize(owner.ClientSize, owner.RenderScaling);
        if (size.Width < 1 || size.Height < 1) return;
        if (bitmap is null || size != pixel_size || scaling != owner.RenderScaling)
        {
            release_bitmap();
            pixel_size = size;
            scaling = owner.RenderScaling;
            bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
            var info = new BitmapInfo
            {
                size = 40, width = size.Width, height = -size.Height,
                planes = 1, bits = 32, image_size = checked((uint)(size.Width * size.Height * 4))
            };
            memory = CreateCompatibleDC(0);
            if (memory == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            dib = CreateDIBSection(memory, ref info, 0, out pixels, 0, 0);
            if (dib == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous_bitmap = SelectObject(memory, dib);
        }
        bitmap.Render(drawing);
        int stride = checked(size.Width * 4);
        bitmap.CopyPixels(new PixelRect(size), pixels, checked(stride * size.Height), stride);
        var origin = owner.PointToScreen(default);
        var destination = new NativePoint { x = origin.X, y = origin.Y };
        var dimensions = new NativeSize { width = size.Width, height = size.Height };
        var source = new NativePoint();
        var blend = new Blend { alpha = 255, format = 1 };
        if (!UpdateLayeredWindow(handle, 0, ref destination, ref dimensions, memory, ref source, 0, ref blend, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        dirty = false;
    }

    void hide()
    {
        timer.Stop();
        dirty = false;
        if (handle != 0) ShowWindow(handle, 0);
        release_bitmap();
    }

    void release_bitmap()
    {
        bitmap?.Dispose();
        bitmap = null;
        if (memory != 0 && previous_bitmap != 0) SelectObject(memory, previous_bitmap);
        if (dib != 0) DeleteObject(dib);
        if (memory != 0) DeleteDC(memory);
        memory = dib = previous_bitmap = pixels = 0;
    }

    public void Close() => Dispose();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        hide();
        release_bitmap();
        if (handle != 0) DestroyWindow(handle);
        handle = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    struct NativeSize { public int width, height; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct Blend { public byte operation, flags, alpha, format; }
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfo
    {
        public uint size;
        public int width, height;
        public ushort planes, bits;
        public uint compression, image_size;
        public int x_resolution, y_resolution;
        public uint colors, important;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateWindowExW(uint extended, string classname, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")]
    static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)]
    static extern nint CreateCompatibleDC(nint source);
    [DllImport("gdi32.dll", SetLastError = true)]
    static extern nint CreateDIBSection(nint device, ref BitmapInfo bitmap, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")]
    static extern nint SelectObject(nint device, nint value);
    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")]
    static extern bool DeleteDC(nint device);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UpdateLayeredWindow(nint window, nint destination_device, ref NativePoint destination, ref NativeSize size,
        nint source_device, ref NativePoint source, uint color, ref Blend blend, uint flags);
}
