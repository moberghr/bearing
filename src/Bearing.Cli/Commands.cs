namespace Bearing.Cli;

/// <summary>
/// The command names and the limits that go with them — one place, because the parser, the help text and
/// the host's own error messages all name them and a drift between those is a command that cannot be
/// found by following the advice that named it.
/// </summary>
public static class Commands
{
    public const string Connections = "connections";
    public const string Tables = "tables";
    public const string Describe = "describe";
    public const string Query = "query";
    public const string Explain = "explain";

    /// <summary>
    /// The default row cap. Small on purpose: the usual caller is an agent putting this into a model's
    /// context, where a thousand rows costs more than it tells anyone. Ask for more when you mean it.
    /// <para>
    /// There is deliberately no ceiling above it. The small default is the protection — it applies when
    /// nobody said otherwise — but an explicit <c>--max-rows</c> is honoured whatever its size: a number
    /// silently replaced by a smaller one gives a result the caller cannot tell from the whole answer.
    /// </para>
    /// </summary>
    public const int DefaultMaxRows = 200;

    /// <summary>
    /// Everything the tool will do, in the order someone discovers it. This is what an agent reads before
    /// choosing a command, so the two facts it cannot find out by trying are in it: the access is read-only,
    /// and a connection is a name and nothing more (§1.11). It must not imply a sandbox.
    /// </summary>
    public static string Help =>
        $"""
        bearing: query the Bearing connections whose owner exposed them, read-only.

          bearing                                  Open the Bearing window.
          bearing [options] <command> [arguments]

        Commands
          {Connections,-33}List the exposed connections: name, engine, environment.
                                           Never the host, port, database, user or password - the
                                           name is the handle every other command takes. Also names
                                           the project it read, and any connections in it that are
                                           not exposed - those only their owner can open up.
          {Tables + " <connection>",-33}Tables and views. --schema <name> narrows it.
          {Describe + " <connection> <table>",-33}Columns with types and nullability, the primary key, and
                                           the foreign keys touching the table in either direction.
          {Query + " <connection> [sql]",-33}Run a read-only query and print its rows. Several
                                           statements run in order, and each is its own result set:
                                           one entry in `results`, or one grid after another. If any
                                           of them fails, nothing is printed but the error.
          {Explain + " <connection> [sql]",-33}Its query plan, as a tree. PostgreSQL connections only.

        Options
          --project <dir>   The Bearing project to read. Defaults to the most recently opened one,
                            which `{Connections}` names; pass it to stay on one project.
          --table           Output aligned for reading. The default is JSON: stable, and what a
                            script should parse. --table and --tsv are not stable.
          --tsv             Output tab-separated: the --table layout, compact enough to read in
                            an agent's context. Tabs, newlines and backslashes in a value are
                            escaped (\t \n \\), and a null is \N, so unlike --table it is unambiguous.
          --schema <name>   For `{Tables}`.
          --file <path>     Read the SQL from a file instead of an argument; - for standard input.
          --out <path>      Write the rows to a .csv or .xlsx file instead of printing them. An xlsx
                            takes one sheet per result set; a CSV holds one table. Rows are unlimited
                            unless you pass --max-rows, because a capped export is a truncated file.
          --max-rows <n>    Rows to return, or 'all'. Defaults to {DefaultMaxRows}; a larger number is
                            honoured rather than clamped.
          --timeout <secs>  Lower the time a statement may run. It cannot raise what the connection
                            already allows.
          --analyze         For `{Explain}`: run the statement and measure it, inside a transaction that
                            is rolled back. Without it nothing is executed.
          --version         Print the version and exit.
          --                Everything after this is an argument, not an option.

        The connection is opened read-only whatever its owner does with it, and only statements this
        engine calls reads are sent at all - anything else is refused before it reaches the server.
        Only connections marked for external access can be queried; a connection that asks for its
        password each session can only be opened in Bearing itself.

        Exit codes: 0 fine, 1 the command could not be completed, 2 the arguments were wrong.
        """;
}
