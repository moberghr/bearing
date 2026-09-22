namespace Bearing.Cli.Tools;

/// <summary>
/// One statement to run, and what the caller asked for around it.
/// <para>
/// A record rather than a parameter list because the run commands take overlapping subsets of the same few
/// things, and a five-argument method whose meaning depends on position is how a connection name ends up in
/// the SQL slot. It is also the shape that grows: the next flag is a property here, not a change to every
/// call site and every fake.
/// </para>
/// </summary>
public sealed record RunRequest(string Connection, string Sql)
{
    /// <summary>Rows to materialise, or null for the command's default. See <c>BearingHost.Rows</c> for how
    /// this combines with <see cref="UnlimitedRows"/> and <see cref="OutPath"/>.</summary>
    public int? MaxRows { get; init; }

    /// <summary>Everything, explicitly asked for (<c>--max-rows all</c>). Separate from a very large
    /// <see cref="MaxRows"/> because "everything" is a different request from "a lot".</summary>
    public bool UnlimitedRows { get; init; }

    /// <summary>Where to write the rows instead of returning them, or null to return them. The extension
    /// has already been checked to be one the exporter knows.</summary>
    public string? OutPath { get; init; }

    /// <summary>
    /// Seconds a statement may run, which may only <b>lower</b> what the connection already allows —
    /// raising it would let a caller lift a limit its owner set (§1.9).
    /// </summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>
    /// For <c>explain</c>: measure rather than only plan, which means the statement actually <b>runs</b>.
    /// Always inside a transaction that is rolled back — not for the obvious write, but because a plain
    /// SELECT can call a volatile function that writes.
    /// </summary>
    public bool Analyze { get; init; }
}
