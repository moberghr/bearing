using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Bearing.App.Services;
using Bearing.App.ViewModels;
using Bearing.App.Views;
using Bearing.Core.Logging;
using Bearing.Core.Workspace;
using Bearing.Data;
using Bearing.App.Demo;
using Bearing.Core.Data;
using Bearing.Demo;
using Bearing.Persistence;
using Bearing.Updates;

namespace Bearing.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Recolor the app-wide <c>ConnectionBrush</c> from the active connection's environment hex.
    /// Mutating the shared brush's color updates every <c>{DynamicResource ConnectionBrush}</c>
    /// consumer at once (tab accent, dots, results accent, status-bar line). Null/invalid → neutral.
    /// </summary>
    public static void SetConnectionAccent(string? environmentHex)
    {
        if (Current?.Resources.TryGetResource("ConnectionBrush", Current.ActualThemeVariant, out var res) == true
            && res is Avalonia.Media.SolidColorBrush brush)
        {
            brush.Color = Theming.ConnectionColors.Resolve(environmentHex);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            LogStartup("framework init done");

            // Resilience: catch UI-thread exceptions (including those escaping async void handlers),
            // log + show them, and keep the app alive. AppDomain/TaskScheduler backstops live in
            // Program.Main. Surface presents the error dialog on the UI thread, owned by the main window.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                CrashReporter.Report("UI thread", e.Exception);
                e.Handled = true;
            };
            CrashReporter.Surface = (context, ex) =>
                Dispatcher.UIThread.Post(() => Views.ErrorDialog.Show(desktop.MainWindow, context, ex));

            // Demo mode (#64): a whole session served from fixed data, in a temp directory that is deleted on
            // the way out. Decided here, once, because it replaces half the object graph — the provider
            // registry, all four stores and the secret store — and a session cannot be half a demo.
            _demo = DemoMode.Requested(desktop.Args) ? DemoWorkspace.Create() : null;

            var settings = new Settings.SettingsService(new AppSettingsStore());
            // The type scale, before any window is built: the tokens in Tokens.axaml carry defaults, and this
            // overwrites them from settings so the first frame is already at the user's size (#52).
            Theming.FontScale.Apply(settings.Current.UiFontSize, settings.Current.GridFontSize);
            // The timezone setting's picker, validation and description live in the app layer: resolving a
            // zone id means TimeZoneInfo, and Core holds abstractions and records only (§2.1, #77).
            Core.Workspace.SettingsCatalog.TimeZoneSuggestions = Formatting.DisplayTimeZone.Available;
            Core.Workspace.SettingsCatalog.TimeZoneValidator = Formatting.DisplayTimeZone.IsKnown;
            Core.Workspace.SettingsCatalog.TimeZoneDescriber = Formatting.DisplayTimeZone.Describe;
            Formatting.CellFormat.Zone = Formatting.DisplayTimeZone.Resolve(settings.Current.DisplayTimeZone);
            // The demo registry holds the demo provider *instead of* Postgres, not beside it: that is what
            // makes the fake unreachable from any normal connection flow, since in an ordinary session it is
            // not in the graph at all.
            // The demo executor is handed the real statement splitter: it lives in Bearing.Sql, which
            // Bearing.Demo may not reference (§2.2), and without it a multi-statement run — which the demo's
            // own welcome script invites — would return one result set instead of two.
            IProviderRegistry providers = _demo is null
                ? new ProviderRegistry()
                : new DemoProvider(DemoExecutor.Default(
                    sql => Bearing.Sql.StatementSplitter.Split(sql).Select(span => span.Text).ToList()));
            IProjectStore projectStore = _demo?.Projects ?? new JsonProjectStore();
            ISessionStore sessionStore = _demo?.Sessions ?? new JsonSessionStore();
            IQueryLog queryLog = _demo?.QueryLog ?? new SqliteQueryLog(
                retentionDays: settings.Current.QueryLogRetentionDays,
                // The dialect's own lexer decides what a literal is; the store only knows it was handed a
                // string (§2.2). Read once at startup, so a mid-session flip cannot leave half the log
                // redacted and half not.
                redactSql: settings.Current.QueryLogRedactLiterals ? Bearing.Sql.SqlRedactor.Redact : null);
            IRecentProjects recentProjects = _demo?.RecentProjects ?? new FileRecentProjects();

            var vm = new ShellViewModel(providers, projectStore, sessionStore, queryLog, recentProjects,
                dialogs: new Views.DialogService(),
                credentialPrompt: new Views.DialogCredentialPrompt(),
                settings: settings);
            // A settings file that can't be written is a status-bar problem, not a crash (§5.2).
            settings.SaveFailed = message => vm.StatusText = message;
            LogStartup("vm created");
            var window = new MainWindow { DataContext = vm };

            // The type scale follows the settings live (#52). XAML reads the tokens through
            // {DynamicResource} and updates itself; the results grid is built in code and reads its sizes
            // once, so it is told to re-render — a font setting that waited for the next query would look
            // broken while the user was still moving the dial.
            settings.Changed += s =>
            {
                Theming.FontScale.Apply(s.UiFontSize, s.GridFontSize);
                // The zone reaches the grid the same way a font size does — the cell text is built in code,
                // so the results have to be re-rendered rather than left to a binding (#77).
                Formatting.CellFormat.Zone = Formatting.DisplayTimeZone.Resolve(s.DisplayTimeZone);
                Dispatcher.UIThread.Post(window.RefreshTypeScale);
            };
            LogStartup("window constructed");

            // Self-update (#20). The coordinator owns the policy (one check per launch, honour the setting,
            // never install on its own); the strip above the status bar is its only surface. Restart is a
            // plain window close so the shutdown pipeline below still runs before the updater swaps the
            // install — an update must never be the reason an unsaved buffer or a live query is lost.
            // Messages cross from the coordinator's background work to the UI thread here, as with
            // SettingsService.SaveFailed above.
            var updates = new UpdateCoordinator(
                new VelopackUpdateService(),
                autoUpdateEnabled: () => settings.Current.AutoUpdate,
                requestShutdown: () => Dispatcher.UIThread.Post(() => window.Close()),
                report: message => Dispatcher.UIThread.Post(() => vm.StatusText = message));
            // Release notes. One source — the GitHub Releases API — feeds all three surfaces: the strip's
            // "what's new?", Help ▸ What's New, and the once-per-upgrade greeting below. Velopack carries
            // notes of its own, but only ever for the version it is offering (see GitHubReleaseNotes), so
            // making it a second source would mean two places to look and two ways to be out of date.
            var releaseNotes = new ReleaseNotesCoordinator(
                new GitHubReleaseNotes(),
                runningVersion: AppVersion.Display,
                lastSeenVersion: () => settings.Current.LastSeenVersion,
                // Posted, not called inline: this runs on the thread pool (see the Opened hook below), and
                // SettingsService.Changed drives UI-bound properties on the shell — IsMenuVisible reaches
                // Menu.IsVisible in XAML — so a direct call would trip Avalonia's VerifyAccess on precisely
                // the launches this feature exists for. It also keeps every settings write on one thread,
                // so a background save can't race the window-size write on the shared .tmp path.
                recordSeen: version => Dispatcher.UIThread.Post(
                    () => settings.Update(s => s with { LastSeenVersion = version })),
                // A settings file that already exists means this user has run Bearing before, even though
                // LastSeenVersion is null — see ShowWhatsNewIfUpdatedAsync.
                isFreshInstall: !File.Exists(settings.Location))
            {
                Show = (notes, focus) => Dispatcher.UIThread.Post(
                    () => Views.ReleaseNotesDialog.Open(window, notes, focus)),
                Report = message => Dispatcher.UIThread.Post(() => vm.StatusText = message),
            };
            vm.Updates = new UpdateViewModel(updates, releaseNotes);
            window.Opened += (_, _) => CrashReporter.Observe(
                Task.Run(() => updates.StartAsync()), "update check");
            // The first launch after an update shows that version's notes, once. Off the UI thread and
            // silent on failure, for the same reason the update check above is: nobody opened the app to be
            // told GitHub was unreachable.
            window.Opened += (_, _) => CrashReporter.Observe(
                Task.Run(() => releaseNotes.ShowWhatsNewIfUpdatedAsync()), "release notes");
            // Hand a staged update over on the way out, not when the user clicks Restart: Closed fires only
            // for a close that actually happened, and MainWindow.OnClosing cancels it while a query is
            // running. Staging any earlier would leave the updater waiting on a process that carries on
            // running. Still early enough — the process is alive, which is what the updater waits for.
            window.Closed += (_, _) =>
            {
                if (updates.ApplyIfPending() is { } failure) CrashLog.Note("update apply-on-exit", failure);
                CleanUpDemo();
            };
            // Every exit path, exactly once, mirroring SaveSession above: a killed process (Ctrl+C in the
            // terminal, IDE stop) never fires Closed, and %TEMP% is not reliably swept on Windows — so
            // without this a demo stopped from the debugger leaves its directory for good.
            desktop.ShutdownRequested += (_, _) => CleanUpDemo();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanUpDemo();

            // Window size is persisted state, not a preference — the setting only decides whether it is
            // replayed. Position is deliberately left to the window manager (Wayland won't honour it
            // anyway), and a maximized window isn't recorded, so un-maximizing returns to a real size.
            if (settings.Current is { RestoreWindowSize: true, WindowWidth: { } w, WindowHeight: { } h }
                && w > 200 && h > 200)
            {
                window.Width = w;
                window.Height = h;
            }
            window.Closing += (_, _) =>
            {
                if (window.WindowState != WindowState.Normal) return;
                settings.Update(s => s with { WindowWidth = window.Width, WindowHeight = window.Height });
            };

            // Persist the session on every exit path, exactly once. A window close fires Closing on
            // the UI thread; a killed process (Ctrl+C in the terminal, IDE stop) shuts the runtime
            // down and runs ProcessExit instead. Save is synchronous (no deadlock) and best-effort;
            // the live editor's text is already mirrored into the tab, so the off-UI-thread
            // ProcessExit path skips FlushActiveEditor (which would touch controls off-thread).
            var saved = 0;
            void SaveSession(bool fromUiThread)
            {
                if (Interlocked.Exchange(ref saved, 1) != 0) return;
                if (fromUiThread) window.FlushActiveEditor();
                vm.SaveWorkspace();
                _ = vm.DisposeSessionsAsync();
            }

            window.Closing += (_, _) => SaveSession(fromUiThread: true);
            desktop.ShutdownRequested += (_, _) => SaveSession(fromUiThread: true);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => SaveSession(fromUiThread: false);

            desktop.MainWindow = window;

            // Optional startup timing (set BEARING_STARTUP_TIMING=1) — measures process start → first
            // window shown, and → project/demo restore complete (the off-thread work).
            if (Environment.GetEnvironmentVariable("BEARING_STARTUP_TIMING") is { Length: > 0 })
                window.Opened += (_, _) => LogStartup("window shown");

            // Resolve the keychain and restore the project OFF the UI thread — never block startup.
            // Observed so a failure to restore is logged + surfaced rather than lost.
            CrashReporter.Observe(InitializeAsync(vm, settings), "startup initialize");
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task InitializeAsync(ShellViewModel vm, Settings.SettingsService settings)
    {
        // One-time cleanup of the removed on-disk secret fallback. Off the UI thread with the rest of startup;
        // it is best-effort and never blocks the app (§5.2).
        LegacySecretFiles.Purge();

        if (_demo is { } demo)
        {
            // The demo's own store, and *before* anything probes the real one: SecretStoreFactory.CreateAsync
            // reaches for the OS keychain, which on Linux can prompt to unlock the keyring — for a session
            // that has no secret to keep. Attaching it here rather than after the probe is what makes the
            // "no keychain call" claim true instead of aspirational (§1.1).
            vm.AttachSecretStore(demo.Secrets, reprobe: _ => Task.FromResult(demo.Secrets));
            // A demo opens its own throwaway project rather than the last-used one: the user's real project
            // is neither read nor written, and there is nothing in the recent list to go back to because
            // this one is never added to it (#64).
            await vm.StartDemoAsync(demo.ProjectDirectory, DemoMode.WelcomeScript);
            LogStartup("demo ready");
            return;
        }

        var secretStore = await SecretStoreFactory.CreateAsync();
        // The same call is handed over as the re-probe: this one runs very early, and a keyring that wasn't
        // serving yet at this instant would otherwise pin the whole session into storing nothing.
        vm.AttachSecretStore(secretStore, reprobe: SecretStoreFactory.CreateAsync);

        // Reopen the last-used project; fall back to the default project on first run (or if it's gone).
        await vm.ResumeLastProjectAsync(DefaultProjectDirectory());
        // Opt-in convenience: seed the local pagila demo connection only when BEARING_SEED_DEMO is set.
        // By default a fresh profile starts as an empty project — no connections, no history.
        if (Environment.GetEnvironmentVariable("BEARING_SEED_DEMO") is { Length: > 0 })
            await vm.Connections.SeedDemoConnectionAsync("localhost", 5434, "pagila", "postgres", "squirrel");
        LogStartup("project ready");
    }

    /// <summary>The demo session's throwaway workspace, or null in an ordinary session (#64).</summary>
    private static Demo.DemoWorkspace? _demo;

    private static int _demoCleaned;

    /// <summary>
    /// Delete the demo's temp directory, once, from whichever exit path gets there first.
    /// <para>
    /// The wait happens on a pool thread, not the caller's: this runs from the UI thread on a window close,
    /// and blocking that thread on work whose continuations want it is a deadlock — the wait would time out
    /// and leave the directory behind. Capped so a stuck delete cannot hold the exit, and swallowed at the
    /// end because a demo that cannot tidy up must not become a crash on the way out (§5.2).
    /// </para>
    /// </summary>
    private static void CleanUpDemo()
    {
        if (_demo is not { } demo) return;
        if (Interlocked.Exchange(ref _demoCleaned, 1) != 0) return;

        try
        {
            if (!Task.Run(() => demo.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(2)))
                CrashLog.Note("demo cleanup", "timed out; the temp directory was left for the OS to collect");
        }
        catch (Exception ex) { CrashLog.Note("demo cleanup", ex.Message); }
    }

    /// <summary>Write a startup milestone (ms since process start) when BEARING_STARTUP_TIMING is set.</summary>
    internal static void LogStartup(string milestone)
    {
        if (Environment.GetEnvironmentVariable("BEARING_STARTUP_TIMING") is not { Length: > 0 }) return;
        try
        {
            var since = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
            Console.Error.WriteLine($"[startup] {milestone}: {since.TotalMilliseconds:F0} ms");
        }
        catch { /* best-effort diagnostic */ }
    }

    private static string DefaultProjectDirectory()
        => Path.Combine(BearingPaths.DataDir, "projects", "default");
}
