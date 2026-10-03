using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Gear360.Gui.Services;
using Gear360.Gui.ViewModels;
using Gear360.Gui.Views;
using Gear360.Platform;

namespace Gear360.Gui;

/// <summary>The Avalonia application: wires the main window to its view model and services.</summary>
public sealed class App : Application
{
    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            window.DataContext = new MainWindowViewModel(
                PlatformCameraDiscovery.Create(),
                new AvaloniaDialogService(window),
                new FfmpegStitchService(),
                new JsonSettingsStore(JsonSettingsStore.DefaultPath),
                new AvaloniaUiDispatcher());
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
