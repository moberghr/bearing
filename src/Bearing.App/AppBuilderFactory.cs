using Avalonia;

namespace Bearing.App;

/// <summary>
/// Shared Avalonia configuration, used by the desktop entry point and the headless UI tests (#62).
/// <para>
/// The two differ in exactly one call — the desktop app detects a real windowing platform, the tests
/// substitute the headless one — so that call is the only thing left to the caller. Everything else must be
/// identical: <see cref="App"/> itself (and with it the token dictionaries every code-built visual resolves
/// through) and the Inter font the whole UI measures against. A test app that configured its own subset would
/// be asserting against a different app than the one that ships.
/// </para>
/// </summary>
public static class AppBuilderFactory
{
    /// <summary>Everything both entry points share; the caller adds the windowing platform.</summary>
    public static AppBuilder Configure() =>
        AppBuilder.Configure<App>()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// The window's X11 class (<c>WM_CLASS</c>), which is how a Linux desktop ties a running window to the
    /// launcher that started it. It has to equal the launcher's <c>StartupWMClass</c>, or GNOME shows the
    /// pinned icon <i>and</i> a second, generic one for the window.
    /// <para>
    /// Stated rather than left to Avalonia, whose default is the entry assembly's name — which silently
    /// became <c>bearing-app</c> when the window's executable was renamed for the CLI (§1.11). It is the
    /// Velopack pack id because <c>vpk</c> writes exactly that into the AppImage's own desktop entry, which
    /// is what an AppImage integrator installs; <c>build/release.sh</c>'s launcher uses the same value.
    /// </para>
    /// </summary>
    public const string WindowClass = "BearingSql";

    /// <summary>The desktop entry point's builder: the shared configuration on the real platform.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        Configure()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = WindowClass });
}
