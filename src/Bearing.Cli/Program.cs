using System.Reflection;
using Bearing.Cli.Tools;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Data;
using Bearing.Persistence;
using Bearing.Results;
using Bearing.Sessions;

namespace Bearing.Cli;

/// <summary>
/// <c>bearing</c> — the one command. With no arguments it opens the Bearing window; with a command it
/// queries the connections their owner exposed (§1.11), from a shell, a script, or an agent that has one.
/// <para>
/// The GUI apphost beside it is called <c>bearing-app</c> so that this one can have the plain name. A
/// console exe has to be the one on <c>PATH</c>: a WinExe cannot write to a pipe or return a useful exit
/// code, and those are the whole of what a command is.
/// </para>
/// <para>
/// It does not talk to a running Bearing and does not need one: it reads the same <c>project.json</c>, the
/// same OS keychain and the same providers the app does, and runs the statement through the same
/// <c>Bearing.Sessions</c> the editor runs one through. What it cannot do is <i>ask</i> anything — there is
/// no window — so a connection whose credential has to be typed is reported as unavailable rather than
/// attempted.
/// </para>
/// <para>
/// <b>Every invocation is a fresh process, and therefore a fresh connection.</b> That is the cost of being
/// a command rather than a server: there is no pool to reuse and <c>tables</c> / <c>describe</c> re-read the
/// catalog each time. It is the right trade for a tool called a few times a minute by a person or an agent,
/// and the wrong one for a loop — which is what <c>--max-rows</c> and a well-aimed <c>query</c> are for.
/// </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = CliParser.Parse(args);

        if (options.Version) { Console.Out.WriteLine(Version()); return CliRunner.Ok; }

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };

        var app = new AppLauncher();

        // Opening the window, printing the help and reporting a usage error all need no project, no keychain
        // and no connection — and must not pay for one. Launching in particular must not: a person typing
        // `bearing` to open the app would otherwise be told their project file is unreadable.
        if (options.LaunchApp || options.Help || options.Error is not null)
            return await CliRunner
                .RunAsync(options, NoHost.Instance, app, Console.Out, Console.Error, stopping.Token)
                .ConfigureAwait(false);

        // Everything that can be cancelled is inside the handler, resolution and host construction
        // included: the keychain probe in HostAsync takes the token, so Ctrl+C during it used to surface
        // as an unhandled exception and a stack trace rather than the line below. Found by review.
        try
        {
            var projectDirectory = ResolveProject(options, out var projectError);
            if (projectError is not null)
            {
                await Console.Error.WriteLineAsync(projectError);
                return CliRunner.Usage;
            }

            await using var session = await HostAsync(projectDirectory, stopping.Token).ConfigureAwait(false);

            return await CliRunner
                .RunAsync(options, session.Host, app, Console.Out, Console.Error, stopping.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Cancelled.");
            return CliRunner.Failed;
        }
    }

    /// <summary>The object graph, built by hand — §2.4's rule holds here too, and this is a second
    /// composition root rather than an exception to the first.</summary>
    private static async Task<HostSession> HostAsync(string? projectDirectory, CancellationToken ct)
    {
        // The user's own settings, read once: the display zone here and the query log's retention and
        // redaction below all come from them.
        var settings = Settings();

        // Before anything can render a cell. CellFormat.Zone is process-wide and every host has to set it
        // (§2.7): left at its UTC default, this command's exports would render a timestamptz in UTC while
        // the app's Export menu rendered the same rows in the user's display zone — two files that disagree
        // with nothing on either to say which is which. The JSON output is unaffected (it carries values,
        // not rendered text), which is exactly why the gap could sit here unnoticed.
        CellFormat.Zone = DisplayZone.Resolve(settings.DisplayTimeZone);

        if (projectDirectory is null) return new HostSession(NoHost.Instance, null);

        // The real keychain. A machine with none yields NoSecretStore, whose reads return null — so a
        // connection with a stored password fails to authenticate and says so, which is the honest outcome
        // and not one this process can improve on by asking (§1.1).
        var secrets = await SecretStoreFactory.CreateAsync(ct).ConfigureAwait(false);

        var providers = new ProviderRegistry();
        // No ICredentialPrompt, and that null is the whole posture of this host: nothing here can raise a
        // dialog, so a kind that would have to is refused up front by ExternalAccessPolicy rather than
        // failing deep inside a connect.
        var credentials = new CredentialResolver(() => secrets, prompt: null, new EntraTokenProvider());
        // No idle sweep: the process outlives one command by milliseconds, and a background timer could only
        // race the teardown.
        var sessions = new ConnectionSessionManager(
            providers, () => credentials, idleTimeout: null, clock: null, runSweepTimer: false);

        // Held, not just handed over: Append returns before the row is written, so the log has to be
        // disposed on the way out or the command's own entry is lost with the process.
        var log = QueryLog(settings);

        return new HostSession(
            new BearingHost(new JsonProjectStore(), projectDirectory, providers, sessions, log),
            sessions,
            log);
    }

    /// <summary>
    /// The same SQLite log the app writes, opened on the same file — which is why it was built for two
    /// processes from the start (WAL, a busy timeout, and migrations that take the write lock up front).
    /// <para>
    /// The user's own settings are honoured rather than defaulted: retention because a log the CLI never
    /// pruned would outgrow the one the app maintains, and redaction because §1.3 makes it a property of
    /// <i>when a row was written</i> — a user who asked for literals to be stripped did not ask for that to
    /// stop applying to statements an agent ran.
    /// </para>
    /// <para>
    /// Null when the log cannot be opened at all. A command that cannot write history still answers the
    /// question it was asked; §5.2's stance, and the app's on the same file.
    /// </para>
    /// </summary>
    private static SqliteQueryLog? QueryLog(AppSettings settings)
    {
        try
        {
            return new SqliteQueryLog(
                retentionDays: settings.QueryLogRetentionDays,
                redactSql: settings.QueryLogRedactLiterals
                    ? (providerId, sql) => ProviderTraits.For(providerId).Dialect.RedactLiterals(sql)
                    : null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The user's settings, or the defaults when they cannot be read. A settings file that is missing,
    /// locked or malformed is not a reason to fail the command someone asked for (§5.2) — it means this
    /// process renders timestamps in UTC and prunes history on the default schedule, which is what a first
    /// run does anyway.
    /// </summary>
    private static AppSettings Settings()
    {
        try { return new AppSettingsStore().Load(); }
        catch { return new AppSettings(); }
    }

    private sealed record HostSession(
        IBearingHost Host, IConnectionSessionManager? Sessions, SqliteQueryLog? Log = null) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // The log first: Append hands the entry to a background writer and returns, so disposing the
            // sessions (or exiting) before it has flushed loses the row the command just produced.
            if (Log is not null) await Log.DisposeAsync().ConfigureAwait(false);
            if (Sessions is not null) await Sessions.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Which project to read: <c>--project</c> when given, else the one most recently opened in Bearing.
    /// <para>
    /// The explicit form is the one to put in a script, because the fallback moves: it is whatever the user
    /// last opened, so a command written against "my project" follows them to a different one. The fallback
    /// exists because it is right far more often than it is wrong, and typing the path every time for the
    /// interactive case is worse.
    /// </para>
    /// </summary>
    /// <param name="recent">The remembered-projects list; the real one when a caller does not say. A
    /// parameter so a test can hand over one that fails, which is the case this method exists to survive.</param>
    internal static string? ResolveProject(
        CliOptions options, out string? error, IRecentProjects? recent = null)
    {
        error = null;

        if (options.ProjectDirectory is { } given)
        {
            // GetFullPath throws on a path the platform cannot express — an invalid character, a path past
            // the limit — and this runs outside Main's handler, which catches only cancellation. A caller
            // who typed a bad path gets the usage error it is, not a stack trace.
            string full;
            try { full = Path.GetFullPath(given); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = $"'{given}' is not a usable path: {ex.Message}";
                return null;
            }

            if (Directory.Exists(full)) return full;
            error = $"No such directory: {full}";
            return null;
        }

        // FileRecentProjects.ListAsync does not swallow: a truncated or hand-edited recent.json throws
        // JsonException and a locked one throws IOException, and Main catches only cancellation — so a plain
        // `bearing connections` exited with a stack trace over a file that is only a convenience. The same
        // class as the --project "" crash, on the path taken when nobody passed --project at all.
        //
        // An unreadable list means "no remembered project", which is what the command already says how to
        // fix: pass --project.
        IReadOnlyList<string> remembered;
        try
        {
            remembered = (recent ?? new FileRecentProjects())
                .ListAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }

        // A remembered project whose folder has gone is a stale list entry rather than an error, and the
        // next one down is very likely the answer.
        return remembered.FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    /// Every command, failing with the same sentence. Used for help and usage errors (where it is never
    /// called) and when no project could be found — reported per command rather than by refusing to start,
    /// so the message reaches whoever ran it instead of a bare exit code.
    /// </summary>
    private sealed class NoHost : IBearingHost
    {
        public static readonly NoHost Instance = new();

        private const string Reason =
            "No Bearing project to read. Pass --project <path to the project directory>, or open a project "
            + "in Bearing once so it becomes the most recent one.";

        public Task<ICliResponse> ListConnectionsAsync(CancellationToken ct) => throw new CommandFailure(Reason);

        public Task<ICliResponse> ListTablesAsync(string connection, string? schema, CancellationToken ct)
            => throw new CommandFailure(Reason);

        public Task<ICliResponse> DescribeTableAsync(string connection, string table, CancellationToken ct)
            => throw new CommandFailure(Reason);

        public Task<ICliResponse> QueryAsync(RunRequest request, CancellationToken ct)
            => throw new CommandFailure(Reason);

        public Task<ICliResponse> ExplainAsync(RunRequest request, CancellationToken ct)
            => throw new CommandFailure(Reason);
    }

    /// <summary>MinVer's version, without the build metadata it appends.</summary>
    private static string Version()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) return "0.0.0";
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
