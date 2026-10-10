using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>
/// A statement cut off after its last token: trailing <c>;</c> terminators and trailing comments go, so
/// a clause can be appended or the statement wrapped as a derived table.
/// <para>
/// Lexer-based because "does the text end in <c>;</c>" is the wrong question. The bug this replaced, from
/// a real script:
/// <code>
/// select … order by l.id;
///
/// -- COMMIT;
/// </code>
/// The last <c>;</c> in the text is the comment's, so the old check stripped that one and kept the real
/// terminator — and the first-page limit went out as <c>… l.id; limit 101</c>, a syntax error at
/// <c>limit</c>. A trailing comment with no semicolon was worse inside EXPLAIN: <c>select 1 -- x</c>
/// became <c>EXPLAIN select 1 -- x;</c>, the terminator commented out.
/// </para>
/// <para>
/// Only the end is touched, so offsets computed on the original text (T-SQL's <c>TOP</c> insertion
/// point) still hold for the result.
/// </para>
/// </summary>
public static class StatementEnd
{
    /// <summary>Postgres: <paramref name="sql"/> up to the end of its last token that is not <c>;</c>,
    /// read with the vendored lexer (comments are on the hidden channel).</summary>
    public static string TrimPostgres(string sql)
    {
        var end = 0;
        foreach (var t in PgParsing.LexAll(sql))
        {
            if (t.Type == TokenConstants.EOF || t.Channel != TokenConstants.DefaultChannel) continue;
            if (t.Text != ";") end = t.StopIndex + 1;
        }
        return sql[..end];
    }

    /// <summary>T-SQL: the same, read with <see cref="TSqlScanner"/> (which drops comments).</summary>
    public static string TrimTSql(string sql)
    {
        var end = 0;
        foreach (var t in TSqlScanner.Tokenize(sql))
            if (t.Text != ";") end = t.Start + t.Length;
        return sql[..end];
    }
}
