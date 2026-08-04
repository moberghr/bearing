namespace Bearing.Sql;

/// <summary>
/// Builds the SQL for one page of a result set. Prefers a top-level <c>LIMIT/OFFSET</c> suffix (via
/// <see cref="FirstPageLimiter"/>) so the query's <c>ORDER BY</c> is honored consistently across pages;
/// falls back to wrapping the query as a derived table when it can't take a safe suffix (its own LIMIT,
/// a locking clause, a multi-statement batch, …). Pure text generation — the executor just runs the
/// string it returns, mirroring how <see cref="DmlGenerator"/> feeds the write executor.
/// </summary>
public static class PageSql
{
    /// <summary>The SQL to fetch <paramref name="limit"/> rows starting at <paramref name="offset"/>:
    /// a top-level suffix when safe, otherwise the derived-table <see cref="Wrap"/>.</summary>
    public static string Page(string sql, int offset, int limit)
        => FirstPageLimiter.TryAppendPage(sql, offset, limit) ?? Wrap(sql, offset, limit);

    /// <summary>Fallback paging that wraps <paramref name="sql"/> as a derived table and pages that.
    /// <paramref name="offset"/>/<paramref name="limit"/> are ints (not user text), so the interpolation
    /// is injection-safe. The query sits on its own lines so a trailing line-comment can't swallow the
    /// wrapper.</summary>
    public static string Wrap(string sql, int offset, int limit)
        => $"select * from (\n{StripTrailingSemicolon(sql)}\n) as _sq offset {offset} limit {limit}";

    private static string StripTrailingSemicolon(string sql)
    {
        var s = sql.TrimEnd();
        return s.EndsWith(';') ? s[..^1] : s;
    }
}
