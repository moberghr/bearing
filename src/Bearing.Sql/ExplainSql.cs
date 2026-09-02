namespace Bearing.Sql;

/// <summary>What to send to the server to explain a statement, and whether it will be rolled back.</summary>
/// <param name="Sql">The batch to execute. One statement for a plain EXPLAIN, three for ANALYZE.</param>
/// <param name="Analyzed">Whether the statement will actually be run.</param>
/// <param name="RolledBack">Whether a transaction wraps it, so nothing it does persists.</param>
public sealed record ExplainRequest(string Sql, bool Analyzed, bool RolledBack);

/// <summary>
/// Builds the <c>EXPLAIN</c> to send for a statement.
/// <para>
/// JSON format throughout, because the tree is the feature — the text format would have to be re-parsed out
/// of indentation, and Postgres already offers the structure.
/// </para>
/// </summary>
public static class ExplainSql
{
    /// <summary>
    /// The plan-only form. Nothing is executed, so nothing needs guarding.
    /// </summary>
    public static ExplainRequest Plan(string statement)
        => new($"EXPLAIN (FORMAT JSON) {Body(statement)}", Analyzed: false, RolledBack: false);

    /// <summary>
    /// The form that measures — and therefore <b>runs</b> the statement.
    /// <para>
    /// Always inside a transaction that is rolled back. Not only for the obvious <c>UPDATE</c>: a plain
    /// <c>SELECT</c> can call a volatile function that writes, and <c>EXPLAIN ANALYZE</c> on it would commit
    /// that. Wrapping everything is both simpler to reason about and the safer of the two rules, and it costs
    /// a read-only plan nothing.
    /// </para>
    /// <para>
    /// A rollback is not a promise that nothing happened: a sequence consumed by <c>nextval</c> does not go
    /// back, and neither does anything a trigger did outside the database. The UI says the plan was rolled
    /// back rather than claiming it was free of consequences.
    /// </para>
    /// <para>
    /// Three statements in one batch rather than an explicit transaction in the data layer, so this stays
    /// pure and testable and the executor needs no new entry point: <c>BEGIN</c> and <c>ROLLBACK</c> return
    /// no rows, so the one result that has any is the plan.
    /// </para>
    /// </summary>
    public static ExplainRequest Measured(string statement)
        => new(
            $"BEGIN;\nEXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) {Body(statement)};\nROLLBACK;",
            Analyzed: true,
            RolledBack: true);

    /// <summary>
    /// The statement itself, without a trailing semicolon.
    /// <para>
    /// Removed because it would otherwise land in the middle of the ANALYZE batch and end the EXPLAIN early,
    /// leaving <c>ROLLBACK</c> as a statement of its own — which parses, runs, and silently produces a plan
    /// that was not rolled back at all.
    /// </para>
    /// </summary>
    private static string Body(string statement)
    {
        var trimmed = (statement ?? "").Trim();
        while (trimmed.EndsWith(';')) trimmed = trimmed[..^1].TrimEnd();
        return trimmed;
    }
}
