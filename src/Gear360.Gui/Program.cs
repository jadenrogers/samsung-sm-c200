using Avalonia;

namespace Gear360.Gui;

internal static class Program
{
    // Avalonia is not ready until AppMain runs; nothing that touches it may run before then.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
