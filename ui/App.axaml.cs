using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using WallAligner.Core;

namespace WallAligner;

public sealed class App : Application
{
    CancellationTokenSource? lifetime;
    WallSession? session;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            lifetime = new();
            session = new(Program.arguments);
            var window = new MainWindow(session);
            desktop.MainWindow = window;
            session.activated += () => Dispatcher.UIThread.Post(() => { window.Show(); window.Activate(); });
            desktop.Exit += (_, _) =>
            {
                lifetime.Cancel();
                session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                lifetime.Dispose();
            };
            _ = start(window, lifetime.Token);
        }
        base.OnFrameworkInitializationCompleted();
    }

    async Task start(MainWindow window, CancellationToken token)
    {
        try { await session!.start(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Dispatcher.UIThread.Post(() => window.show_status(error.Message)); }
    }
}
