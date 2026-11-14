using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Goggles.Core;
using Goggles.Desktop.Platform;
using Goggles.Desktop.Services;
using Goggles.Desktop.ViewModels;
using Goggles.Desktop.Views;

namespace Goggles.Desktop;

public partial class App : Application
{
    private GogSession? _session;
    private CoverImageCache? _covers;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _session = GogSession.Create();
            _covers = new CoverImageCache();

            var shell = new MainViewModel(_session, _covers);

            desktop.MainWindow = new MainWindow { DataContext = shell };
            desktop.ShutdownRequested += (_, _) =>
            {
                _session?.Dispose();
                _covers?.Dispose();
            };

            // After start-up, so Avalonia's menu bar already exists to be added to. Without an Edit
            // menu, ⌘V does nothing inside the embedded sign-in browser: macOS routes editing
            // shortcuts through the main menu before they reach the focused view.
            Dispatcher.UIThread.Post(MacEditMenu.Install, DispatcherPriority.Background);

            // Fire and forget: the window opens immediately and fills in as the library arrives.
            _ = shell.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
