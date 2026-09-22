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
    public int? MaxRows { get; init; }
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
                    if (Next(args, ref i) is not { } rows) return Missing("--max-rows", "a number of rows");
                    if (!int.TryParse(rows, out var parsed) || parsed < 1)
                        return new CliOptions { Error = $"--max-rows needs a positive whole number, not '{rows}'." };
                    // Clamped rather than refused: asking for a million rows is a judgement about how much
                    // you want, not a mistake worth failing over, and the output says how many came back.
                    options = options with { MaxRows = Math.Min(parsed, Commands.MaxRowsCeiling) };
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
        if (command is not (Commands.Connections or Commands.Tables or Commands.Describe or Commands.Query))
            return new CliOptions { Error = $"Unknown command '{command}'." };

        var rest = positional.Skip(1).ToList();
        if (Arity(command) is { } expected && rest.Count != expected.Count)
            return new CliOptions { Error = $"`{command}` takes {expected.Description}." };

        return options with { Command = command, Arguments = rest };
    }

    /// <summary>
    /// How many positional arguments each command takes. Exact rather than "at least", because the shape
    /// that motivates it is a quoted SQL string the shell split: <c>bearing query db select 1</c>
    /// would otherwise run <c>select</c> and silently drop the rest.
    /// </summary>
    private static (int Count, string Description)? Arity(string command) => command switch
    {
        Commands.Connections => (0, "no arguments"),
        Commands.Tables => (1, "a connection name"),
        Commands.Describe => (2, "a connection name and a table name"),
        Commands.Query => (2, "a connection name and one quoted SQL statement"),
        _ => null,
    };

    private static string? Next(IReadOnlyList<string> args, ref int i)
        => i + 1 < args.Count ? args[++i] : null;

    private static CliOptions Missing(string option, string what)
        => new() { Error = $"{option} needs {what}." };
}
