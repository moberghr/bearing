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

    /// <summary>
    /// The default row cap. Small on purpose: the usual caller is an agent putting this into a model's
    /// context, where a thousand rows costs more than it tells anyone. Ask for more when you mean it.
    /// </summary>
    public const int DefaultMaxRows = 200;

    /// <summary>The most one call returns, however much is asked for.</summary>
    public const int MaxRowsCeiling = 1_000;

    /// <summary>
    /// Everything the tool will do, in the order someone discovers it. This is what an agent reads before
    /// deciding what to run, so it carries the two facts it cannot find out by trying: the access is
    /// read-only, and a connection is a name and nothing more (§1.11). It must not imply a sandbox.
    /// </summary>
    public static string Help =>
        $"""
        bearing: query the Bearing connections whose owner exposed them, read-only.

          bearing [--project <dir>] [--table] <command> [arguments]

        Commands
          {Connections}                        List the exposed connections: name, engine, environment.
                                     Never the host, port, database, user or password - the name is
                                     the handle every other command takes.
          {Tables} <connection>          List the tables and views in that connection's database.
                                     --schema <name> restricts it to one schema.
          {Describe} <connection> <table>  Columns with types and nullability, the primary key, and the
                                     foreign keys touching the table in either direction.
          {Query} <connection> <sql>     Run a read-only query and print its rows.
                                     --max-rows <n> up to {MaxRowsCeiling}; the default is {DefaultMaxRows}.

        Options
          --project <dir>   The Bearing project to read. Defaults to the most recently opened one.
          --table           Human-readable output. The default is JSON, which is what a script or an
                            agent should parse; it is stable, and --table is not.
          --schema <name>   For `{Tables}`.
          --max-rows <n>    For `{Query}`.
          --version         Print the version and exit.
          --                Everything after this is an argument, not an option. Needed only for SQL
                            that starts with a line comment and contains no whitespace.

        The connection is opened read-only whatever its owner does with it, so INSERT, UPDATE, DELETE
        and DDL are refused rather than run. Only connections marked for external access are visible;
        a connection that asks for its password each session can only be opened in Bearing itself.

        Exit codes: 0 fine, 1 the command could not be completed, 2 the arguments were wrong.
        """;
}
