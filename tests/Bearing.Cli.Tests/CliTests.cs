using System.Text.Json;
using System.Text.Json.Nodes;
using Bearing.Cli.Tools;
using Xunit;

namespace Bearing.Cli.Tests;

/// <summary>
/// The command line itself: what each invocation is understood to mean, what it prints, and what it exits
/// with. An argument misread here is a query aimed at the wrong connection, so this is the surface that
/// most wants pinning.
/// </summary>
public class CliTests
{
    // ---- parsing ------------------------------------------------------------------------------

    [Fact]
    public void A_command_and_its_arguments_are_read_in_order()
    {
        var options = CliParser.Parse(["query", "reporting", "select 1", "--max-rows", "25"]);

        Assert.Null(options.Error);
        Assert.Equal(Commands.Query, options.Command);
        Assert.Equal(["reporting", "select 1"], options.Arguments);
        Assert.Equal(25, options.MaxRows);
    }

    [Fact]
    public void Options_may_come_before_or_after_the_command()
    {
        var before = CliParser.Parse(["--project", "/tmp/p", "--table", "tables", "reporting"]);
        var after = CliParser.Parse(["tables", "reporting", "--table", "--project", "/tmp/p"]);

        foreach (var options in new[] { before, after })
        {
            Assert.Null(options.Error);
            Assert.Equal(Commands.Tables, options.Command);
            Assert.Equal(["reporting"], options.Arguments);
            Assert.Equal("/tmp/p", options.ProjectDirectory);
            Assert.Equal(OutputFormat.Table, options.Format);
        }
    }

    [Fact]
    public void Json_is_the_format_unless_a_person_asks_otherwise()
    {
        // The default is what a script and an agent parse, and it is the one that stays stable.
        Assert.Equal(OutputFormat.Json, CliParser.Parse(["connections"]).Format);
        Assert.Equal(OutputFormat.Json, CliParser.Parse(["connections", "--json"]).Format);
        Assert.Equal(OutputFormat.Table, CliParser.Parse(["connections", "--table"]).Format);
    }

    /// <summary>
    /// The shape this exists for: a SQL string the shell split because it was not quoted. Accepting extra
    /// positionals would run `select` and silently drop the rest of the statement.
    /// </summary>
    [Fact]
    public void An_unquoted_sql_statement_is_refused_rather_than_truncated()
    {
        var options = CliParser.Parse(["query", "reporting", "select", "1", "from", "film"]);

        Assert.NotNull(options.Error);
        Assert.Contains("quoted", options.Error);
        Assert.Null(options.Command);
    }

    [Theory]
    [InlineData(new[] { "tables" }, "a connection name")]
    [InlineData(new[] { "describe", "reporting" }, "a table name")]
    [InlineData(new[] { "connections", "extra" }, "no arguments")]
    public void A_command_given_the_wrong_number_of_arguments_says_what_it_takes(string[] args, string expected)
    {
        var options = CliParser.Parse(args);

        Assert.NotNull(options.Error);
        Assert.Contains(expected, options.Error);
    }

    /// <summary>
    /// SQL that opens with a line comment is ordinary SQL, and it was being reported as an unknown option
    /// — a usage error with nothing the caller could do about it. Found by review.
    /// </summary>
    [Fact]
    public void Sql_that_starts_with_a_line_comment_is_not_mistaken_for_an_option()
    {
        var options = CliParser.Parse(["query", "db", "-- rows added today\nselect count(*) from film"]);

        Assert.Null(options.Error);
        Assert.Equal(Commands.Query, options.Command);
        Assert.StartsWith("-- rows added today", options.Arguments[1]);
    }

    [Fact]
    public void A_bare_double_dash_ends_the_options()
    {
        // The escape for the one shape the whitespace rule cannot help with: a comment-only statement.
        var options = CliParser.Parse(["query", "db", "--", "--just-a-comment"]);

        Assert.Null(options.Error);
        Assert.Equal(["db", "--just-a-comment"], options.Arguments);
    }

    [Fact]
    public void A_mistyped_option_is_still_reported()
    {
        // The whitespace rule must not turn every typo into SQL.
        Assert.NotNull(CliParser.Parse(["query", "db", "select 1", "--tabel"]).Error);
    }

    [Fact]
    public void An_unknown_command_or_option_is_named_in_the_error()
    {
        Assert.Contains("'explain'", CliParser.Parse(["explain", "reporting"]).Error);
        Assert.Contains("'--rows'", CliParser.Parse(["query", "r", "select 1", "--rows", "5"]).Error);
    }

    [Fact]
    public void An_option_missing_its_value_says_which_one()
    {
        Assert.Contains("--project", CliParser.Parse(["connections", "--project"]).Error);
        Assert.Contains("--max-rows", CliParser.Parse(["query", "r", "select 1", "--max-rows"]).Error);
    }

    [Fact]
    public void A_row_cap_beyond_the_ceiling_is_clamped_and_a_nonsense_one_is_refused()
    {
        // Asking for a million rows is a judgement about how much you want, not a mistake worth failing
        // over; a negative or non-numeric one cannot be honoured at all.
        Assert.Equal(Commands.MaxRowsCeiling, CliParser.Parse(["query", "r", "s", "--max-rows", "999999"]).MaxRows);
        Assert.NotNull(CliParser.Parse(["query", "r", "s", "--max-rows", "0"]).Error);
        Assert.NotNull(CliParser.Parse(["query", "r", "s", "--max-rows", "lots"]).Error);
    }

    /// <summary>
    /// `bearing` on its own opens the window. The name is the app's before it is the command's, and typing
    /// an application's name is how people open applications — the commands are what the *arguments* select.
    /// </summary>
    [Fact]
    public void No_arguments_at_all_opens_the_app()
    {
        var options = CliParser.Parse([]);

        Assert.True(options.LaunchApp);
        Assert.False(options.Help);
        Assert.Null(options.Command);
    }

    [Fact]
    public void Options_without_a_command_print_the_help_rather_than_opening_a_window()
    {
        // Someone part-way through typing a command. Opening a window at them would be a strange answer,
        // and it is not what they asked for.
        var options = CliParser.Parse(["--project", "/tmp/p"]);

        Assert.True(options.Help);
        Assert.False(options.LaunchApp);
    }

    [Fact]
    public void Asking_for_help_is_not_asking_for_the_app()
    {
        Assert.True(CliParser.Parse(["--help"]).Help);
        Assert.False(CliParser.Parse(["--help"]).LaunchApp);
    }

    // ---- running ------------------------------------------------------------------------------

    [Fact]
    public async Task Each_command_reaches_the_host_with_what_it_was_given()
    {
        var host = new RecordingHost();

        await Cli.RunAsync(host, "query", "reporting", "select 1", "--max-rows", "25");

        Assert.Equal([Commands.Query], host.Calls);
        Assert.Equal("reporting", host.Connection);
        Assert.Equal("select 1", host.Sql);
        Assert.Equal(25, host.MaxRows);
    }

    [Fact]
    public async Task An_absent_row_cap_is_left_for_the_host_to_default()
    {
        var host = new RecordingHost();

        await Cli.RunAsync(host, "query", "reporting", "select 1");

        Assert.Null(host.MaxRows);
    }

    [Fact]
    public async Task A_schema_filter_reaches_the_tables_command_and_is_absent_otherwise()
    {
        var host = new RecordingHost();
        await Cli.RunAsync(host, "tables", "reporting", "--schema", "public");
        Assert.Equal("public", host.Schema);

        var unfiltered = new RecordingHost();
        await Cli.RunAsync(unfiltered, "tables", "reporting");
        Assert.Null(unfiltered.Schema);
    }

    /// <summary>
    /// The three exit codes are the interface for anything scripting this, and the distinction between the
    /// last two is the one that matters: a script retrying a usage error retries forever.
    /// </summary>
    [Fact]
    public async Task The_exit_code_says_which_kind_of_outcome_it_was()
    {
        Assert.Equal(CliRunner.Ok, (await Cli.RunAsync(new RecordingHost(), "connections")).ExitCode);
        Assert.Equal(CliRunner.Usage, (await Cli.RunAsync(new RecordingHost(), "explain", "x")).ExitCode);

        var refused = new RecordingHost { FailWith = "reporting is read-only - UPDATE not run." };
        Assert.Equal(CliRunner.Failed, (await Cli.RunAsync(refused, "query", "r", "update t set a=1")).ExitCode);
    }

    /// <summary>
    /// A caller that reads stdout must get data or nothing — never an explanation it could parse as data.
    /// </summary>
    [Fact]
    public async Task A_refusal_goes_to_stderr_and_leaves_stdout_empty()
    {
        var host = new RecordingHost { FailWith = "reporting is read-only - UPDATE not run." };

        var run = await Cli.RunAsync(host, "query", "reporting", "update t set a = 1");

        Assert.Empty(run.Out);
        Assert.Contains("read-only", run.Error);
    }

    [Fact]
    public async Task A_usage_error_is_reported_without_touching_the_host()
    {
        var host = new RecordingHost();

        var run = await Cli.RunAsync(host, "query", "reporting");

        Assert.Empty(host.Calls);
        Assert.Empty(run.Out);
        Assert.Contains("--help", run.Error);
    }

    [Fact]
    public async Task Help_names_every_command_and_says_the_access_is_read_only()
    {
        var run = await Cli.RunAsync(new RecordingHost(), "--help");

        Assert.Equal(CliRunner.Ok, run.ExitCode);
        foreach (var command in new[] { Commands.Connections, Commands.Tables, Commands.Describe, Commands.Query })
            Assert.Contains(command, run.Out);

        // The two facts a caller cannot discover by trying (§1.11).
        Assert.Contains("read-only", run.Out);
        Assert.Contains("refused", run.Out);
    }

    [Fact]
    public async Task A_bare_invocation_launches_the_app_and_touches_nothing_else()
    {
        var host = new RecordingHost();
        var app = new FakeAppLauncher();

        var run = await Cli.RunAsync(host, app);

        Assert.Equal(CliRunner.Ok, run.ExitCode);
        Assert.Equal(1, app.Launches);
        // No project is read and no connection is opened: a person typing `bearing` to open the app must not
        // be told their project file is unreadable.
        Assert.Empty(host.Calls);
        Assert.Empty(run.Out);
        Assert.Empty(run.Error);
    }

    [Fact]
    public async Task An_app_that_will_not_start_says_so_and_fails()
    {
        var app = new FakeAppLauncher { FailWith = "Could not find the Bearing app beside this command." };

        var run = await Cli.RunAsync(new RecordingHost(), app);

        Assert.Equal(CliRunner.Failed, run.ExitCode);
        Assert.Contains("Could not find", run.Error);
        Assert.Empty(run.Out);
    }

    [Fact]
    public async Task A_command_never_opens_the_app()
    {
        var app = new FakeAppLauncher();

        await Cli.RunAsync(new RecordingHost(), app, "connections");

        Assert.Equal(0, app.Launches);
    }

    // ---- output -------------------------------------------------------------------------------

    [Fact]
    public async Task The_default_output_is_json_a_script_can_parse()
    {
        var host = new RecordingHost
        {
            Answer = new JsonObject { ["connections"] = new JsonArray { new JsonObject { ["name"] = "reporting" } } },
        };

        var run = await Cli.RunAsync(host, "connections");

        var parsed = JsonNode.Parse(run.Out)!;
        Assert.Equal("reporting", parsed["connections"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Non_ascii_text_is_printed_as_itself_rather_than_as_escape_sequences()
    {
        // The default JSON encoder escapes every non-ASCII character into \uXXXX. That is for embedding
        // JSON in HTML; here it would make a column of Croatian names unreadable to person and model alike.
        var host = new RecordingHost
        {
            Answer = new JsonObject { ["note"] = "račun pročitan" },
        };

        var run = await Cli.RunAsync(host, "connections");

        Assert.Contains("račun pročitan", run.Out);
        Assert.DoesNotContain("\\u", run.Out);
    }

    [Fact]
    public async Task The_table_format_lays_the_same_json_out_for_a_person()
    {
        var host = new RecordingHost
        {
            Answer = new JsonObject
            {
                ["connections"] = new JsonArray
                {
                    new JsonObject { ["name"] = "reporting", ["engine"] = "PostgreSQL", ["access"] = "read-only" },
                    new JsonObject { ["name"] = "warehouse", ["engine"] = "SQL Server", ["access"] = "read-only" },
                },
            },
        };

        var run = await Cli.RunAsync(host, "connections", "--table");

        var lines = run.Out.Split('\n');
        Assert.Contains("name", lines[0]);
        Assert.Contains("engine", lines[0]);
        Assert.StartsWith("----", lines[1]);
        Assert.Contains("reporting", run.Out);
        Assert.Contains("SQL Server", run.Out);
        // Not JSON — this is the whole point of the flag.
        Assert.DoesNotContain("{", run.Out);
    }

    /// <summary>
    /// An optional field present on one row and not another still gets a column. A header taken from the
    /// first row alone would drop <c>unavailable_reason</c> exactly when it is the thing worth reading.
    /// </summary>
    [Fact]
    public async Task A_column_only_some_rows_have_is_still_shown()
    {
        var host = new RecordingHost
        {
            Answer = new JsonObject
            {
                ["connections"] = new JsonArray
                {
                    new JsonObject { ["name"] = "reporting", ["available"] = true },
                    new JsonObject
                    {
                        ["name"] = "prompted",
                        ["available"] = false,
                        ["unavailable_reason"] = "asks for its password each session",
                    },
                },
            },
        };

        var run = await Cli.RunAsync(host, "connections", "--table");

        Assert.Contains("unavailable_reason", run.Out);
        Assert.Contains("asks for its password", run.Out);
    }

    [Fact]
    public async Task A_result_set_prints_its_rows_and_says_when_it_was_cut_short()
    {
        var host = new RecordingHost
        {
            Answer = new JsonObject
            {
                ["results"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["columns"] = new JsonArray
                        {
                            new JsonObject { ["name"] = "id", ["type"] = "integer" },
                            new JsonObject { ["name"] = "title", ["type"] = "text" },
                        },
                        ["rows"] = new JsonArray
                        {
                            new JsonArray { 1, "ACADEMY" },
                            new JsonArray { 2, null },
                        },
                        ["row_count"] = 2,
                        ["truncated"] = true,
                        ["duration_ms"] = 88,
                    },
                },
            },
        };

        var run = await Cli.RunAsync(host, "query", "reporting", "select 1", "--table");

        Assert.Contains("ACADEMY", run.Out);
        Assert.Contains("2 rows in 88 ms", run.Out);
        Assert.Contains("cut short", run.Out);
    }

    [Fact]
    public async Task An_empty_listing_says_so_rather_than_printing_a_bare_header()
    {
        var host = new RecordingHost { Answer = new JsonObject { ["connections"] = new JsonArray() } };

        var run = await Cli.RunAsync(host, "connections", "--table");

        Assert.Contains("(none)", run.Out);
    }
}
