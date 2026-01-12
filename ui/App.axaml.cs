using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using WallAligner.Core;

namespace WallAligner;

public class App : Application
{
    private Extension? extension;
    private MainWindow? window;
    private IClassicDesktopStyleApplicationLifetime? desktop;
    private bool running;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktoplifetime)
        {
            desktop = desktoplifetime;
            Task.Run(start);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async void start()
    {
        try
        {
            extension = new Extension();

            extension.Activated += () =>
            {
                Dispatcher.UIThread.Invoke(() =>
                {
                    if (window == null)
                    {
                        window = new MainWindow(extension);
                        window.Closing += onclose;
                        if (desktop != null)
                            desktop.MainWindow = window;
                    }
                    window.Show();
                    window.Activate();
                });
            };

            running = true;
            extension.Run();
        }
        catch { }
        finally
        {
            running = false;
            await Task.Delay(2000);
            desktop?.Shutdown();
        }
    }

    private void onclose(object? sender, WindowClosingEventArgs e)
    {
        if (e.CloseReason is not WindowCloseReason.WindowClosing)
            return;

        if (!running)
        {
            desktop?.Shutdown();
        }
        else
        {
            e.Cancel = true;
            if (sender is Window w)
                w.Hide();
        }
    }
}
