using Avalonia;

namespace Squirrel.App;

/// <summary>Shared Avalonia configuration, used by the desktop entry point and headless tests.</summary>
public static class AppBuilderFactory
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
