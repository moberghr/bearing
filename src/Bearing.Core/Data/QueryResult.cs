namespace Bearing.Core.Data;

/// <summary>
/// One result column. When the column maps straight to a table column (not an expression/alias), its
/// catalog origin is carried alongside it — the hook for FK navigation and inline edit. Origin comes in
/// two forms because the two engines hand it over differently, and neither can produce the other's:
/// <list type="bullet">
/// <item><b>By id</b> — <see cref="BaseTableId"/> + <see cref="BaseColumnOrdinal"/>. Postgres gets this
/// for free off the RowDescription (table OID + attribute number), and it is exact: it survives a table
/// being renamed mid-session and never needs a name lookup.</item>
/// <item><b>By name</b> — <see cref="BaseSchemaName"/> + <see cref="BaseTableName"/> +
/// <see cref="BaseColumnName"/>, qualified by <see cref="BaseCatalogName"/>. <c>SqlDataReader</c> only
/// ever exposes names, and only when the command was run under <c>CommandBehavior.KeyInfo</c>; there is
/// no id to be had. The resolver looks these up in the schema snapshot (case-insensitively) to reach the
/// same <c>TableInfo</c>/<c>ColumnInfo</c> — and refuses when the catalog is not the one the snapshot
/// describes, because a name is only unique <em>within</em> a database.</item>
/// </list>
/// Both are absent for expression/aliased columns, and origin is populated only for a raw query, not the
/// wrapped paging query.
/// </summary>
public sealed record ColumnDescriptor(
    string Name,
    string DataTypeName,
    Type ClrType,
    long BaseTableId = 0,
    int BaseColumnOrdinal = 0,
    string? BaseSchemaName = null,
    string? BaseTableName = null,
    string? BaseColumnName = null,
    string? BaseCatalogName = null)
{
    /// <summary>True when this column maps straight to a catalog table column, in either origin form.
    /// A name origin without <see cref="BaseColumnName"/> does not count: the table alone can't be
    /// edited through, so it has to read as an expression rather than as a half-mapped column.</summary>
    public bool HasBaseColumn
        => (BaseTableId != 0 && BaseColumnOrdinal > 0)
        || (BaseTableName is not null && BaseColumnName is not null);
}

public sealed record QueryError(string Message, string? SqlState, int? Position);

/// <summary>A fully-materialized query result (small/medium result sets). Large ones stream instead.</summary>
public sealed record QueryResult(
    IReadOnlyList<ColumnDescriptor> Columns,
    IReadOnlyList<object?[]> Rows,
    long RowCount,
    TimeSpan Duration,
    string? Message,
    QueryError? Error,
    bool Truncated)
{
    public bool Success => Error is null;

    /// <summary>
    /// Which statement of the run produced this result — 0-based, and set <b>only when the provider can
    /// prove the mapping</b>. It answers "where did this grid come from" for one set out of a batch, where
    /// the run's own SQL text is the whole batch and names no single set.
    /// <para>
    /// An index rather than the statement's text, and deliberately: locating a statement in a buffer needs a
    /// SQL lexer, which lives in <c>Bearing.Sql</c> — a project the data layer may not reference (§2.2). So a
    /// provider reports the position it is certain of and the caller resolves the text, which also keeps this
    /// provider-neutral (like <see cref="ColumnDescriptor.BaseTableId"/>).
    /// </para>
    /// <para>
    /// Null means <i>unattributable, not unattempted</i>: the caller must fall back to the whole run's text
    /// rather than assume position in the result list is the statement number. A provider sets it only where
    /// it holds — see the Postgres executor, where a batch that returns fewer result sets than it has
    /// statements (a write among the selects) has no public way to say which ones were skipped.
    /// </para>
    /// </summary>
    public int? StatementIndex { get; init; }
}

/// <summary>
/// One chunk of a streamed result (<see cref="IQueryExecutor.StreamRowsAsync"/>). Batches arrive in row
/// order and are meant to be appended as they land, so a long read shows progress instead of one jump at the
/// end. <see cref="Truncated"/> is set only on the final batch, and only when the read stopped at
/// <see cref="QueryOptions.MaxRows"/> with rows still waiting on the server — that is how a caller tells
/// "this is the whole result" from "this is where the ceiling cut it", without a second query.
/// </summary>
public sealed record RowBatch(IReadOnlyList<object?[]> Rows, bool Truncated);

public sealed record QueryOptions
{
    /// <summary>Cap materialized rows (UI grid protection). Null = unlimited.</summary>
    public int? MaxRows { get; init; } = 10_000;

    /// <summary>Rows per batch when streaming (<see cref="IQueryExecutor.StreamRowsAsync"/>); ignored by the
    /// materializing paths. Sets how often a long read reports progress, nothing else.</summary>
    public int BatchRows { get; init; } = 1_000;
}
