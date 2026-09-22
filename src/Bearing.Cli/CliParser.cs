namespace Bearing.Cli;

/// <summary>How output is rendered. JSON is the default — see <see cref="CliParser"/>.</summary>
public enum OutputFormat
{
    /// <summary>What a script or an agent parses. Stable: fields are added, never renamed or reordered
    /// out from under a caller.</summary>
    Json,

    /// <summary>For a person at a terminal. Deliberately <b>not</b> stable — it is free to change shape,
    /// which is the whole reason JSON is what everything else reads.</summary>
    Table,
}

/// <summary>What one invocation asked for, or why it could not be understood.</summary>
public sealed record CliOptions
{
    public string? Command { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? ProjectDirectory { get; init; }
    public OutputFormat Format { get; init; } = OutputFormat.Json;
    public string? Schema { get; init; }

    /// <summary>Rows to return, or null for the command's own default. An explicit number is honoured
    /// whatever its size — see <see cref="Commands.DefaultMaxRows"/> for why there is no ceiling.</summary>
    public int? MaxRows { get; init; }

    /// <summary>`--max-rows all`.</summary>
    public bool UnlimitedRows { get; init; }

    /// <summary>A SQL file to run, or <c>-</c> for standard input. Mutually exclusive with SQL given
    /// positionally.</summary>
    public string? File { get; init; }

    /// <summary>Where to write the rows. The extension picks the format, and has already been checked.</summary>
    public string? Out { get; init; }

    /// <summary>Seconds a statement may run. May only <b>lower</b> what the connection allows (§1.9).</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>`explain --analyze`: run the statement and measure it, rather than only planning it.</summary>
    public bool Analyze { get; init; }
    public bool Help { get; init; }
    public bool Version { get; init; }

    /// <summary>
    /// Set only when there were <b>no arguments at all</b>: `bearing` on its own opens the window, because
    /// that is what typing an application's name does and the name is the app's before it is the command's.
    /// <para>
    /// Deliberately not set when options were given without a command — `bearing --project x` is someone
    /// part-way through typing a command, and opening a window at them would be a strange answer to it.
    /// That case prints the help.
    /// </para>
    /// </summary>
    public bool LaunchApp { get; init; }

    /// <summary>Set when the arguments could not be understood; nothing else is meaningful then.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Turns an argv into a <see cref="CliOptions"/>, and nothing else — no I/O, no environment, no clock.
/// Pure so every shape of invocation is a unit test rather than a process (§2.5), which matters more here
/// than usual: an argument this misreads is a query aimed at the wrong connection.
/// </summary>
public static class CliParser
{
    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new CliOptions { LaunchApp = true };

        var options = new CliOptions();
        var positional = new List<string>();
        var optionsEnded = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            // Everything after a bare `--` is positional, whatever it starts with. SQL that opens with a
            // line comment is the case this exists for: a statement beginning `-- rows today` was reported
            // as `Unknown option`, which is a usage error the caller cannot act on for a statement that is
            // perfectly ordinary. Found by review.
            if (optionsEnded) { positional.Add(arg); continue; }
            if (arg == "--") { optionsEnded = true; continue; }

            switch (arg)
            {
                case "--help" or "-h":
                    return options with { Help = true };

                case "--version":
                    return options with { Version = true };

                case "--table":
                    options = options with { Format = OutputFormat.Table };
                    break;

                case "--json":
                    // Accepted though it is the default, because a caller that says what it wants should
                    // not be told it is wrong for being explicit.
                    options = options with { Format = OutputFormat.Json };
                    break;

                case "--project":
                    if (Next(args, ref i) is not { } directory) return Missing("--project", "a directory");
                    options = options with { ProjectDirectory = directory };
                    break;

                case "--schema":
                    if (Next(args, ref i) is not { } schema) return Missing("--schema", "a schema name");
                    options = options with { Schema = schema };
                    break;

                case "--max-rows":
                    if (Next(args, ref i) is not { } rows)
                        return Missing("--max-rows", "a number of rows, or 'all'");
                    if (string.Equals(rows, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        options = options with { UnlimitedRows = true, MaxRows = null };
                        break;
                    }

                    if (!int.TryParse(rows, out var parsed) || parsed < 1)
                        return new CliOptions
                        {
                            Error = $"--max-rows needs a positive whole number or 'all', not '{rows}'.",
                        };
                    // Honoured, not clamped. The small default is the protection, and it applies when
                    // nobody said otherwise; an explicit number is a judgement about how much you want, and
                    // silently returning a smaller one gives a result the caller cannot tell from the
                    // whole answer.
                    options = options with { MaxRows = parsed, UnlimitedRows = false };
                    break;

                case "--file":
                    if (Next(args, ref i) is not { } file)
                        return Missing("--file", "a path, or - for standard input");
                    options = options with { File = file };
                    break;

                case "--out":
                    if (Next(args, ref i) is not { } outPath) return Missing("--out", "a path");
                    if (ExportFormats.For(outPath) is null)
                        return new CliOptions
                        {
                            Error = $"--out needs a .csv or .xlsx path; '{outPath}' is neither.",
                        };
                    options = options with { Out = outPath };
                    break;

                case "--timeout":
                    if (Next(args, ref i) is not { } secs)
                        return Missing("--timeout", "a number of seconds");
                    if (!int.TryParse(secs, out var timeout) || timeout < 1)
                        return new CliOptions
                        {
                            Error = $"--timeout needs a positive whole number of seconds, not '{secs}'.",
                        };
                    options = options with { TimeoutSeconds = timeout };
                    break;

                case "--analyze" or "--analyse":
                    options = options with { Analyze = true };
                    break;

                default:
                    // An option name never contains whitespace, so an argument that does is SQL rather
                    // than a mistyped flag — which is what makes `--` a convenience rather than the only
                    // way to pass a statement that opens with a comment.
                    if (arg.StartsWith('-') && arg.Length > 1 && !arg.Any(char.IsWhiteSpace))
                        return new CliOptions { Error = $"Unknown option '{arg}'." };
                    positional.Add(arg);
                    break;
            }
        }

        if (positional.Count == 0) return options with { Help = true };

        var command = positional[0];
        if (command is not (Commands.Connections or Commands.Tables or Commands.Describe
                            or Commands.Query or Commands.Explain))
            return new CliOptions { Error = $"Unknown command '{command}'." };

        options = options with { Command = command, Arguments = positional.Skip(1).ToList() };
        return Validate(options) ?? options;
    }

    /// <summary>
    /// What each command needs, once the options are known.
    /// <para>
    /// Not a table of argument counts, because the rule that matters cannot be expressed as one: a run
    /// command takes its SQL positionally <i>or</i> from <c>--file</c>, never both and never neither. The
    /// counts are still exact rather than "at least", for the shape that motivated them — a quoted SQL
    /// string the shell split, where <c>bearing query db select 1</c> would otherwise run <c>select</c>
    /// and drop the rest.
    /// </para>
    /// </summary>
    private static CliOptions? Validate(CliOptions o)
    {
        var runsSql = o.Command is Commands.Query or Commands.Explain;

        // An option that means nothing for this command is a mistake worth naming, not one to ignore: a
        // caller who typed it believes it is doing something.
        if (!runsSql && o.File is not null) return Error($"`{o.Command}` does not take --file.");
        if (!runsSql && o.Out is not null) return Error($"`{o.Command}` does not take --out.");
        if (!runsSql && o.TimeoutSeconds is not null) return Error($"`{o.Command}` does not take --timeout.");
        if (o.Command != Commands.Tables && o.Schema is not null)
            return Error($"`{o.Command}` does not take --schema.");
        if (o.Command != Commands.Explain && o.Analyze)
            return Error($"`{o.Command}` does not take --analyze.");
        if (o.Command == Commands.Explain && o.Out is not null)
            return Error("`explain` prints a plan; --out writes rows, so the two do not go together.");

        switch (o.Command)
        {
            case Commands.Connections when o.Arguments.Count != 0:
                return Error("`connections` takes no arguments.");

            case Commands.Tables when o.Arguments.Count != 1:
                return Error("`tables` takes a connection name.");

            case Commands.Describe when o.Arguments.Count != 2:
                return Error("`describe` takes a connection name and a table name.");

            case Commands.Query or Commands.Explain:
                if (o.Arguments.Count == 0) return Error($"`{o.Command}` takes a connection name.");
                if (o.Arguments.Count > 2)
                    return Error($"`{o.Command}` takes a connection name and one quoted SQL statement.");

                var inline = o.Arguments.Count == 2;
                if (inline && o.File is not null)
                    return Error(
                        "Give the SQL either as an argument or with --file, not both — one of them would "
                        + "have to be ignored, and neither is the obvious one.");
                if (!inline && o.File is null)
                    return Error($"`{o.Command}` needs SQL: quote it as an argument, or pass --file <path>.");
                break;
        }

        return null;
    }

    private static CliOptions Error(string message) => new() { Error = message };

    private static string? Next(IReadOnlyList<string> args, ref int i)
        => i + 1 < args.Count ? args[++i] : null;

    private static CliOptions Missing(string option, string what)
        => new() { Error = $"{option} needs {what}." };
}
