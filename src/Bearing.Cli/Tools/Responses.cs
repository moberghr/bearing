namespace Bearing.Cli.Tools;

/// <summary>
/// What a command answers with. A marker, so the host's signature says "a response" rather than "some
/// JSON" and each command's shape is a type the compiler checks.
/// <para>
/// These were hand-built <c>JsonObject</c> literals first, which was wrong on the terms this codebase
/// already holds everywhere else: a key is a string nobody checks, an omitted field fails silently, and the
/// output shape — which is the CLI's public contract — was readable only by reading the code that emitted
/// it. Serialisation happens once, in <c>CliRunner</c>.
/// </para>
/// </summary>
public interface ICliResponse;

// ---- connections -------------------------------------------------------------------------------

public sealed record ConnectionsResponse(IReadOnlyList<ExposedConnection> Connections) : ICliResponse;

/// <summary>
/// One exposed connection. Deliberately <b>not</b> host, port, database or user: the name is the handle,
/// and §1.11 turns on this record carrying nothing that says where the server is. The environment is the
/// one exception, because the user's own word for how dangerous a connection is makes a caller behave
/// better, not worse.
/// </summary>
public sealed record ExposedConnection(string Name, string Engine, string Access, bool Available)
{
    public string? Environment { get; init; }

    /// <summary>Why it cannot be opened without Bearing running, when it cannot.</summary>
    public string? UnavailableReason { get; init; }
}

// ---- catalog -----------------------------------------------------------------------------------

public sealed record TablesResponse(
    IReadOnlyList<string> Schemas,
    IReadOnlyList<RelationSummary> Tables,
    int TableCount) : ICliResponse
{
    /// <summary>Set when the listing was cut at the cap. Null rather than false when it was not, so it is
    /// absent from the output — a listing silently cut reads exactly like a database with that many
    /// relations, which is the thing this flag exists to prevent.</summary>
    public bool? Truncated { get; init; }
}

public sealed record RelationSummary(string Schema, string Name, string Kind);

public sealed record TableResponse(
    string Schema,
    string Name,
    string Kind,
    IReadOnlyList<ColumnSummary> Columns,
    IReadOnlyList<ForeignKeySummary> ForeignKeys) : ICliResponse;

public sealed record ColumnSummary(string Name, string Type, bool NotNull, bool PrimaryKey);

/// <summary>A key, with <b>both</b> ends named in full — the list carries the keys pointing at a table as
/// well as the ones it declares, and "which tables reference this" cannot be answered from one side.</summary>
public sealed record ForeignKeySummary(string Name, KeySide From, KeySide To);

public sealed record KeySide(string Table, IReadOnlyList<string> Columns);

// ---- rows --------------------------------------------------------------------------------------

public sealed record QueryResponse(IReadOnlyList<ResultSet> Results) : ICliResponse;

/// <summary>
/// One result set. <see cref="Truncated"/> means the read stopped at the row ceiling with rows still
/// waiting — not "there are more rows somewhere", which is a different and weaker claim.
/// </summary>
public sealed record ResultSet(
    IReadOnlyList<ColumnHeader> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int RowCount,
    bool Truncated,
    long DurationMs)
{
    /// <summary>What a statement with no grid said for itself ("SELECT 0", an affected-row count). Absent
    /// for an ordinary result.</summary>
    public string? Message { get; init; }
}

public sealed record ColumnHeader(string Name, string Type);

// ---- export ------------------------------------------------------------------------------------

/// <summary>What a <c>--out</c> run wrote. The full path, because a caller that passed a relative one
/// should not have to work out where it landed.</summary>
public sealed record ExportResponse(string Written, string Format, int Results, long Rows) : ICliResponse
{
    /// <summary>The row ceiling stopped the read with rows still waiting, so the file is part of the answer.
    /// Always present rather than omitted when false: a script that writes a file and does not check this
    /// would act on a truncated export, and the reassuring case is the one worth stating.</summary>
    public bool Truncated { get; init; }
}

// ---- plans -------------------------------------------------------------------------------------

public sealed record PlanResponse(bool Analyzed, bool RolledBack, PlanNode Plan) : ICliResponse
{
    public double? PlanningMs { get; init; }
    public double? ExecutionMs { get; init; }

    /// <summary>The worst nodes first — named rather than left to the reader, because "find the expensive
    /// node" is the whole reason to ask for a plan and doing it over a nested tree is the fiddly part.</summary>
    public IReadOnlyList<PlanNode> Hotspots { get; init; } = [];
}

/// <summary>
/// One plan node. Every measurement is nullable and omitted when absent: a plan that was not analysed has
/// no actual rows, which is a different thing from having none.
/// </summary>
public sealed record PlanNode(string Node)
{
    public string? Relation { get; init; }
    public string? Alias { get; init; }
    public string? Index { get; init; }
    public string? Filter { get; init; }
    public double? EstimatedCost { get; init; }
    public double? EstimatedRows { get; init; }
    public double? ActualRows { get; init; }
    public double? ActualMs { get; init; }
    public double? SelfMs { get; init; }
    public double? Loops { get; init; }
    public double? BlocksRead { get; init; }

    /// <summary>Absent rather than empty for a leaf, so a tree does not carry a `children: []` on every
    /// scan node.</summary>
    public IReadOnlyList<PlanNode>? Children { get; init; }
}
