using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;

namespace WallAligner;

public sealed class OverlayPointer : IDisposable
{
    delegate nint InputHook(int code, nint message, nint data);
    readonly InputHook callback;
    readonly InputHook keyboard_callback;
    nint hook;
    nint keyboard_hook;
    PixelRect bounds;
    IReadOnlyList<PixelRect> excluded = [];
    bool dragging;
    public KeyModifiers modifiers { get; private set; }
    public event Action<int, PixelPoint>? pan;
    public event Action<PixelPoint>? clicked;
    public event Action? modifiers_changed;

    [StructLayout(LayoutKind.Sequential)]
    struct MouseData
    {
        public int x;
        public int y;
        public uint buttons;
        public uint flags;
        public uint time;
        public nuint extra;
    }

    public OverlayPointer()
    {
        callback = read;
        keyboard_callback = read_keyboard;
    }

    public void update(PixelRect area, IReadOnlyList<PixelRect> exclusions)
    {
        bounds = area;
        excluded = exclusions;
        if (hook != 0) return;
        hook = SetWindowsHookExW(14, callback, GetModuleHandleW(null), 0);
        if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        keyboard_hook = SetWindowsHookExW(13, keyboard_callback, GetModuleHandleW(null), 0);
        if (keyboard_hook == 0)
        {
            int error = Marshal.GetLastWin32Error();
            disable();
            throw new Win32Exception(error);
        }
        modifiers = current_modifiers();
    }

    KeyModifiers current_modifiers(int key = 0, bool down = false)
    {
        bool pressed(int code) => key == code ? down : GetAsyncKeyState(code) < 0;
        bool held(int general, int left, int right) => key == general ? down : pressed(left) || pressed(right);
        var value = KeyModifiers.None;
        if (held(0x12, 0xA4, 0xA5)) value |= KeyModifiers.Alt;
        if (held(0x11, 0xA2, 0xA3)) value |= KeyModifiers.Control;
        if (held(0x10, 0xA0, 0xA1)) value |= KeyModifiers.Shift;
        return value;
    }

    void change_modifiers(KeyModifiers value)
    {
        if (modifiers == value) return;
        modifiers = value;
        modifiers_changed?.Invoke();
    }

    nint read_keyboard(int code, nint message, nint data)
    {
        if (code >= 0 && (int)message is 0x100 or 0x101 or 0x104 or 0x105)
        {
            int key = Marshal.ReadInt32(data);
            if (key is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5)
                change_modifiers(current_modifiers(key, (int)message is 0x100 or 0x104));
        }
        return CallNextHookEx(keyboard_hook, code, message, data);
    }

    nint read(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            var value = Marshal.PtrToStructure<MouseData>(data);
            if ((int)message == 0x201)
                change_modifiers(current_modifiers());
            if (route((int)message, new PixelPoint(value.x, value.y))) return 1;
        }
        return CallNextHookEx(hook, code, message, data);
    }

    bool route(int message, PixelPoint point)
    {
        bool inside = bounds.Contains(point) && !excluded.Any(x => x.Contains(point));
        if (message == 0x204 && inside)
        {
            dragging = true;
            pan?.Invoke(0, point);
            return true;
        }
        if (dragging && message == 0x200)
        {
            pan?.Invoke(1, point);
            return false;
        }
        if (dragging && message == 0x205)
        {
            dragging = false;
            pan?.Invoke(2, point);
            return true;
        }
        if (message == 0x201 && inside) clicked?.Invoke(point);
        return false;
    }

    public void disable()
    {
        if (hook != 0) UnhookWindowsHookEx(hook);
        if (keyboard_hook != 0) UnhookWindowsHookEx(keyboard_hook);
        hook = 0;
        keyboard_hook = 0;
        modifiers = KeyModifiers.None;
        if (dragging) pan?.Invoke(2, default);
        dragging = false;
    }

    public void Dispose() => disable();

    [DllImport("user32.dll", SetLastError = true)]
    static extern nint SetWindowsHookExW(int id, InputHook callback, nint module, uint thread);
    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern nint GetModuleHandleW(string? name);
}
