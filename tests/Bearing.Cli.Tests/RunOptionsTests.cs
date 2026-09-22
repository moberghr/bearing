using Bearing.Cli;
using Bearing.Cli.Tools;
using Bearing.Core.Workspace;
using Xunit;

namespace Bearing.Cli.Tests;

/// <summary>
/// The flags the run commands take — <c>--file</c>, <c>--out</c>, <c>--timeout</c>, <c>--analyze</c> — and
/// where the SQL comes from.
/// <para>
/// This is the code that decides <b>what statement gets sent to a production server</b>, which is why
/// <c>CliParser</c> is pure: an argument misread here is a query aimed at the wrong connection or a file
/// written to the wrong path, and neither announces itself. <c>--max-rows</c> was pinned from the start and
/// the rest of the surface was not; this is the rest.
/// </para>
/// </summary>
public class RunOptionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bearing-cli-args", Guid.NewGuid().ToString("N"));
    private readonly TextReader _stdin = Console.In;

    public RunOptionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Console.SetIn(_stdin);
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---- parsing ------------------------------------------------------------------------------

    [Fact]
    public void Every_run_flag_is_read_into_the_request()
    {
        var options = CliParser.Parse(
            ["query", "reporting", "select 1", "--out", "report.csv", "--timeout", "5"]);

        Assert.Null(options.Error);
        Assert.Equal("report.csv", options.Out);
        Assert.Equal(5, options.TimeoutSeconds);
    }

    [Fact]
    public void A_file_is_read_in_place_of_the_sql_argument()
    {
        var options = CliParser.Parse(["query", "reporting", "--file", "monthly.sql"]);

        Assert.Null(options.Error);
        Assert.Equal("monthly.sql", options.File);
        Assert.Equal(["reporting"], options.Arguments);
    }

    [Fact]
    public void Analyze_is_explains_alone()
    {
        Assert.Null(CliParser.Parse(["explain", "reporting", "select 1", "--analyze"]).Error);
        Assert.Contains("does not take --analyze", CliParser.Parse(["query", "r", "select 1", "--analyze"]).Error);
    }

    /// <summary>
    /// An option that means nothing for the command it was typed on is named rather than ignored: the
    /// caller believes it is doing something, and silently dropping it is how a script runs uncapped or
    /// unwritten for a week before anybody notices.
    /// </summary>
    [Theory]
    [InlineData(new[] { "connections", "--file", "x.sql" }, "does not take --file")]
    [InlineData(new[] { "tables", "db", "--out", "x.csv" }, "does not take --out")]
    [InlineData(new[] { "describe", "db", "t", "--timeout", "5" }, "does not take --timeout")]
    [InlineData(new[] { "connections", "--max-rows", "20" }, "does not take --max-rows")]
    [InlineData(new[] { "tables", "db", "--max-rows", "20" }, "does not take --max-rows")]
    [InlineData(new[] { "explain", "db", "select 1", "--max-rows", "20" }, "does not take --max-rows")]
    [InlineData(new[] { "query", "db", "select 1", "--schema", "public" }, "does not take --schema")]
    public void An_option_that_means_nothing_here_is_refused_by_name(string[] args, string expected)
    {
        var options = CliParser.Parse(args);

        Assert.NotNull(options.Error);
        Assert.Contains(expected, options.Error);
    }

    /// <summary>A plan is not rows, so there is nothing for <c>--out</c> to write — and writing the plan
    /// text to a .csv would be a file no CSV reader can parse.</summary>
    [Fact]
    public void Explain_and_out_are_refused_together()
    {
        var options = CliParser.Parse(["explain", "reporting", "select 1", "--out", "plan.csv"]);

        Assert.Contains("--out writes rows", options.Error);
    }

    /// <summary>Both would mean one of them is ignored, and neither is the obvious one to drop.</summary>
    [Fact]
    public void Sql_given_twice_is_refused_rather_than_one_of_them_being_picked()
    {
        var options = CliParser.Parse(["query", "reporting", "select 1", "--file", "monthly.sql"]);

        Assert.Contains("not both", options.Error);
    }

    [Fact]
    public void Sql_given_no_way_at_all_says_both_ways_to_give_it()
    {
        var options = CliParser.Parse(["query", "reporting"]);

        Assert.NotNull(options.Error);
        Assert.Contains("--file", options.Error);
    }

    /// <summary>Only <c>.csv</c> and <c>.xlsx</c> can be written, and the path is checked before anything
    /// connects — finding out after a minute of reading rows would be the same error, later and dearer.</summary>
    [Theory]
    [InlineData("report.txt")]
    [InlineData("report")]
    [InlineData("report.json")]
    public void An_out_path_that_is_not_a_known_format_is_refused(string path)
    {
        var options = CliParser.Parse(["query", "reporting", "select 1", "--out", path]);

        Assert.NotNull(options.Error);
        Assert.Contains(path, options.Error);
    }

    [Theory]
    [InlineData("report.csv")]
    [InlineData("REPORT.CSV")]
    [InlineData("report.xlsx")]
    public void The_two_formats_are_accepted_whatever_their_case(string path)
    {
        Assert.Null(CliParser.Parse(["query", "reporting", "select 1", "--out", path]).Error);
    }

    /// <summary>
    /// A shell that expanded a variable to nothing hands over an <i>empty</i> argument, not a missing one,
    /// and everything downstream treated that as a value it was given: <c>bearing --project "$PROJ"</c> with
    /// <c>$PROJ</c> unset reached <c>Path.GetFullPath("")</c> and came out as an unhandled
    /// <c>ArgumentException</c> and a stack trace, on the ordinary path, instead of the usage error it is.
    /// </summary>
    [Theory]
    [InlineData("--project")]
    [InlineData("--file")]
    [InlineData("--out")]
    [InlineData("--schema")]
    [InlineData("--max-rows")]
    [InlineData("--timeout")]
    public void An_option_given_a_blank_value_is_a_usage_error_rather_than_a_crash(string option)
    {
        var options = CliParser.Parse(["query", "reporting", "select 1", option, ""]);

        Assert.NotNull(options.Error);
        Assert.Contains(option, options.Error);
    }

    [Fact]
    public async Task A_blank_project_exits_as_a_usage_error_and_prints_no_stack_trace()
    {
        var run = await Cli.RunAsync(new RecordingHost(), "--project", "", "connections");

        Assert.Equal(CliRunner.Usage, run.ExitCode);
        Assert.DoesNotContain("Exception", run.Error);
        Assert.Empty(run.Out);
    }

    // ---- where the SQL comes from -------------------------------------------------------------

    [Fact]
    public async Task A_file_supplies_the_statement()
    {
        var path = Path.Combine(_dir, "monthly.sql");
        await File.WriteAllTextAsync(path, "select count(*) from payment\n");
        var host = new RecordingHost();

        var run = await Cli.RunAsync(host, "query", "reporting", "--file", path);

        Assert.Equal(CliRunner.Ok, run.ExitCode);
        Assert.Equal("select count(*) from payment\n", host.Sql);
        Assert.Equal("reporting", host.Connection);
    }

    /// <summary>The whole file, not its first line: a statement spanning several lines is the ordinary
    /// reason to keep one in a file at all.</summary>
    [Fact]
    public async Task A_multi_line_file_arrives_whole()
    {
        var path = Path.Combine(_dir, "wide.sql");
        const string sql = "select p.amount,\n       c.last_name\n  from payment p\n  join customer c using (customer_id)";
        await File.WriteAllTextAsync(path, sql);
        var host = new RecordingHost();

        await Cli.RunAsync(host, "query", "reporting", "--file", path);

        Assert.Equal(sql, host.Sql);
    }

    [Fact]
    public async Task A_dash_reads_standard_input()
    {
        Console.SetIn(new StringReader("select 1 from dual\n"));
        var host = new RecordingHost();

        var run = await Cli.RunAsync(host, "query", "reporting", "--file", "-");

        Assert.Equal(CliRunner.Ok, run.ExitCode);
        Assert.Equal("select 1 from dual\n", host.Sql);
    }

    /// <summary>The path is the caller's, so naming it is most of the diagnosis — and the host is never
    /// reached, so nothing connects for a statement that does not exist.</summary>
    [Fact]
    public async Task A_file_that_cannot_be_read_is_reported_with_its_path()
    {
        var missing = Path.Combine(_dir, "nope.sql");
        var host = new RecordingHost();

        var run = await Cli.RunAsync(host, "query", "reporting", "--file", missing);

        Assert.Equal(CliRunner.Failed, run.ExitCode);
        Assert.Contains(missing, run.Error);
        Assert.Empty(run.Out);
        Assert.Empty(host.Calls);
    }

    /// <summary>A statement of nothing is not a query, and the server's complaint about it would read
    /// worse than this one. Whitespace counts as empty — a file holding a newline is the common accident.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   \n\t\n")]
    public async Task An_empty_file_is_refused_rather_than_sent(string contents)
    {
        var path = Path.Combine(_dir, "blank.sql");
        await File.WriteAllTextAsync(path, contents);
        var host = new RecordingHost();

        var run = await Cli.RunAsync(host, "query", "reporting", "--file", path);

        Assert.Equal(CliRunner.Failed, run.ExitCode);
        Assert.Contains("no SQL", run.Error);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task Empty_standard_input_is_refused_and_says_where_it_looked()
    {
        Console.SetIn(new StringReader("\n"));
        var host = new RecordingHost();

        var run = await Cli.RunAsync(host, "query", "reporting", "--file", "-");

        Assert.Equal(CliRunner.Failed, run.ExitCode);
        Assert.Contains("standard input", run.Error);
        Assert.Empty(host.Calls);
    }

    // ---- what reaches the host ----------------------------------------------------------------

    [Fact]
    public async Task The_export_path_and_the_timeout_reach_the_host_as_asked()
    {
        var host = new RecordingHost();

        await Cli.RunAsync(host, "query", "reporting", "select 1", "--out", "report.xlsx", "--timeout", "7");

        Assert.Equal("report.xlsx", host.Request!.OutPath);
        Assert.Equal(7, host.Request.TimeoutSeconds);
        // Not defaulted here: how an export's row cap is decided is the host's rule (an export is
        // unlimited), and the parser pre-empting it would put that decision in two places.
        Assert.Null(host.Request.MaxRows);
        Assert.False(host.Request.UnlimitedRows);
    }

    [Fact]
    public async Task Analyze_reaches_explain_and_is_false_when_it_was_not_asked_for()
    {
        var measured = new RecordingHost();
        await Cli.RunAsync(measured, "explain", "reporting", "select 1", "--analyze");
        Assert.True(measured.Request!.Analyze);

        var planned = new RecordingHost();
        await Cli.RunAsync(planned, "explain", "reporting", "select 1");
        Assert.False(planned.Request!.Analyze);
    }
    /// <summary>
    /// The recent-projects list is a convenience, and reading it must not be able to end the command.
    /// <c>FileRecentProjects.ListAsync</c> does not swallow — a truncated or hand-edited <c>recent.json</c>
    /// throws <c>JsonException</c>, a locked one throws <c>IOException</c> — and <c>Main</c> catches only
    /// cancellation, so a plain <c>bearing connections</c> exited with a stack trace over a file nobody
    /// asked it to read. The same class as the <c>--project ""</c> crash, on the default path.
    /// </summary>
    [Fact]
    public void An_unreadable_recent_projects_list_is_no_project_rather_than_a_crash()
    {
        var options = CliParser.Parse(["connections"]);

        var resolved = Program.ResolveProject(options, out var error, new ThrowingRecentProjects());

        Assert.Null(resolved);      // "no remembered project", which the command already says how to fix
        Assert.Null(error);         // and not a usage error: the caller did nothing wrong
    }

    /// <summary>An explicit <c>--project</c> never reads the list at all, so a broken one cannot affect the
    /// form anybody should be putting in a script.</summary>
    [Fact]
    public void An_explicit_project_does_not_touch_the_remembered_list()
    {
        var options = CliParser.Parse(["--project", _dir, "connections"]);

        var resolved = Program.ResolveProject(options, out var error, new ThrowingRecentProjects());

        Assert.Equal(Path.GetFullPath(_dir), resolved);
        Assert.Null(error);
    }

    private sealed class ThrowingRecentProjects : IRecentProjects
    {
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
            => throw new System.Text.Json.JsonException("recent.json is truncated");

        public Task AddAsync(string directory, CancellationToken ct) => Task.CompletedTask;

        public Task RemoveAsync(string directory, CancellationToken ct) => Task.CompletedTask;
    }

}
