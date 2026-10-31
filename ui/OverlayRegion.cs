using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace WallAligner;

public sealed class OverlayRegion : IDisposable
{
    readonly Window window;
    (int l, int t, int r, int b)[]? last;
    public static bool available => OperatingSystem.IsWindows();

    public OverlayRegion(Window window) => this.window = window;

    public void update(bool enabled, IEnumerable<Rect> areas)
    {
        if (!available || window.TryGetPlatformHandle() is not { } handle) return;
        if (!enabled)
        {
            if (last is not null) SetWindowRgn(handle.Handle, IntPtr.Zero, false);
            last = null;
            return;
        }
        double scale = window.RenderScaling;
        var rectangles = areas.Where(r => r.Width > 0 && r.Height > 0).Select(r => (
            l: (int)Math.Floor(r.Left * scale), t: (int)Math.Floor(r.Top * scale),
            r: (int)Math.Ceiling(r.Right * scale), b: (int)Math.Ceiling(r.Bottom * scale))).ToArray();
        if (last is not null && rectangles.AsSpan().SequenceEqual(last)) return;
        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) return;
        try
        {
            foreach (var rect in rectangles)
            {
                IntPtr piece = CreateRectRgn(rect.l, rect.t, rect.r, rect.b);
                if (piece == IntPtr.Zero) throw new InvalidOperationException("Could not create the overlay input region.");
                try
                {
                    if (CombineRgn(region, region, piece, 2) == 0)
                        throw new InvalidOperationException("Could not combine overlay input regions.");
                }
                finally { DeleteObject(piece); }
            }
            if (SetWindowRgn(handle.Handle, region, false) == 0)
                throw new InvalidOperationException("Could not apply the overlay input region.");
            region = IntPtr.Zero;
            last = rectangles;
        }
        finally { if (region != IntPtr.Zero) DeleteObject(region); }
    }

    public void Dispose() => update(false, []);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    static extern int CombineRgn(IntPtr target, IntPtr first, IntPtr second, int mode);
    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")]
    static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
}
