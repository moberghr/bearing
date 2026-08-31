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

    /// <summary>The desktop entry point's builder: the shared configuration on the real platform.</summary>
    public static AppBuilder BuildAvaloniaApp() => Configure().UsePlatformDetect();
}
