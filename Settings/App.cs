using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Settings;

public sealed partial class App : Application
{
    SettingsWindow? mainWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow = new SettingsWindow();
            desktop.MainWindow = mainWindow;
        }
        base.OnFrameworkInitializationCompleted();
        // Shell-launched instances occasionally start hidden although the
        // lifetime shows them: force the window visible once the loop runs.
        Dispatcher.UIThread.Post(() => mainWindow?.Present(), DispatcherPriority.Background);
    }
}
