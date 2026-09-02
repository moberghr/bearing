namespace Bearing.Sql;

/// <summary>
/// Builds the SQL for one page of a result set, and the derived-table wrappers behind it. Prefers a
/// top-level page suffix (via the dialect, <see cref="FirstPageLimiter"/> for Postgres) so the query's
/// <c>ORDER BY</c> is honored consistently across pages; falls back to wrapping the query as a derived
/// table when it can't take a safe suffix (its own LIMIT, a locking clause, a multi-statement batch, …).
/// Pure text generation — the executor just runs the string it returns, mirroring how
/// <see cref="DmlGenerator"/> feeds the write executor.
/// <para>
/// The parameterless-dialect overloads are the Postgres-bound entry points; the wrapper text they
/// produce is the same text <see cref="PostgresDialect"/> serves, so there is one definition of each.
/// </para>
/// </summary>
public static class PageSql
{
    /// <summary>The SQL to fetch <paramref name="limit"/> rows starting at <paramref name="offset"/>,
    /// for Postgres.</summary>
    public static string Page(string sql, int offset, int limit)
        // Non-null by construction: the Postgres dialect never refuses a wrap.
        => Page(PostgresDialect.Instance, sql, offset, limit)!;

    /// <summary>The SQL to fetch <paramref name="limit"/> rows starting at <paramref name="offset"/>:
    /// the dialect's top-level suffix when it will take one, otherwise its derived-table wrap — and
    /// <c>null</c> when the dialect refuses both, which means this query cannot be paged on this engine
    /// and the caller must retire paging rather than send SQL the server rejects.</summary>
    public static string? Page(ISqlDialect dialect, string sql, int offset, int limit)
        => dialect.TryAppendPage(sql, offset, limit) ?? dialect.Wrap(sql, offset, limit);

    /// <summary>Postgres fallback paging that wraps <paramref name="sql"/> as a derived table and pages
    /// that. <paramref name="offset"/>/<paramref name="limit"/> are ints (not user text), so the
    /// interpolation is injection-safe. The query sits on its own lines so a trailing line-comment can't
    /// swallow the wrapper.</summary>
    public static string Wrap(string sql, int offset, int limit)
        => $"select * from (\n{StripTrailingSemicolon(sql)}\n) as _sq offset {offset} limit {limit}";

    /// <summary>Postgres total-row count over an arbitrary query, same derived-table shape as
    /// <see cref="Wrap"/> minus the paging. A query whose <em>shape</em> can't be wrapped at all (a
    /// batch, a data-modifying CTE) fails on the server; that is the executor's call to report as "no
    /// total available", not this function's to predict.</summary>
    public static string CountWrap(string sql)
        => $"select count(*) from (\n{StripTrailingSemicolon(sql)}\n) as _sq";

    private static string StripTrailingSemicolon(string sql)
    {
        var s = sql.TrimEnd();
        return s.EndsWith(';') ? s[..^1] : s;
    }
}
