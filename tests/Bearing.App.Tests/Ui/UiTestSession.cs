using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The application the headless UI tests run against (#62): the real <see cref="Bearing.App.App"/> — its token
/// dictionaries, control themes and the Inter font — on Avalonia's in-process headless windowing platform.
/// <para>
/// <see cref="AppBuilderFactory.Configure"/> is shared with the desktop entry point on purpose; the platform
/// is the only difference. <c>App.OnFrameworkInitializationCompleted</c> builds nothing here: its whole body
/// is guarded on <c>IClassicDesktopStyleApplicationLifetime</c>, so a UI test touches no query log, settings
/// file or update check.
/// </para>
/// <para>
/// Drawing goes through Skia rather than the headless stub because the stub does no text shaping, and text
/// measurement is load-bearing in the results grid — initial column widths are derived from it (#30), and cell
/// ellipsization and scroll offsets follow from it. A stub that measured every string the same would agree
/// with itself and not with the app.
/// </para>
/// </summary>
internal static class UiTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilderFactory.Configure()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// The one headless dispatcher thread the UI tests share, started once and torn down when the collection
/// finishes. <see cref="Run"/> is the only way in: Avalonia state is thread-affine, so a test body has to
/// execute on that thread rather than on xunit's.
/// <para>
/// Isolation is <see cref="AvaloniaTestIsolationLevel.PerTest"/> — each <see cref="Run"/> gets a fresh
/// <see cref="Application"/>. Not a preference: <c>App.SetConnectionAccent</c> mutates the shared
/// <c>ConnectionBrush</c> in place (§9.3), so a reused application would leak one test's environment hue
/// into the next.
/// </para>
/// </summary>
public sealed class UiTestSession : IDisposable
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.StartNew(typeof(UiTestApp), AvaloniaTestIsolationLevel.PerTest);

    /// <summary>Run a test body on the Avalonia dispatcher thread. Assertion failures surface through the
    /// returned task, so a test method is written as <c>public Task Name() =&gt; _ui.Run(() =&gt; …);</c>.</summary>
    public Task Run(Action body) => _session.Dispatch(body, CancellationToken.None);

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// Every UI test lives in this collection so they never run concurrently. Two headless sessions at once means
/// two Avalonia applications in one process, and the framework's per-thread setup is not built for that.
/// <para>
/// The cost is that UI tests serialize — with a fresh application per test on top of it. Keep the suite
/// focused on what only a realized visual tree can answer (a property on a live cell, a scroll offset after a
/// layout pass); pure logic still belongs in a plain unit test over a helper (§2.5).
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiTestCollection : ICollectionFixture<UiTestSession>
{
    public const string Name = "avalonia-headless-ui";
}
