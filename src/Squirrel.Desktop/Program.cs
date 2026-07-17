using Avalonia;
using Squirrel.App;

namespace Squirrel.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilderFactory.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
}
