using Avalonia;

namespace WallAligner;

internal static class Program
{
    internal static string[] arguments = [];

    [STAThread]
    public static void Main(string[] args)
    {
        arguments = args;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
