using System.Collections.Generic;

namespace Bearing.Sql;

/// <summary>
/// The keyword token types the formatter uppercases.
/// <para>
/// A curated list rather than "every token the grammar gave a literal name to", because Postgres has some
/// four hundred keywords and many of the non-reserved ones — <c>name</c>, <c>value</c>, <c>type</c>,
/// <c>text</c> — are ordinary column names. Uppercasing those would be noise.
/// </para>
/// <para>
/// Case here is never a correctness question, only a cosmetic one: Postgres folds unquoted identifiers to
/// lower case, so <c>SELECT FILTER FROM t</c> and <c>select filter from t</c> name the same column. A
/// quoted identifier is a different token type (<c>QuotedIdentifier</c>) and is never touched, which is
/// where case would actually matter.
/// </para>
/// </summary>
internal static class SqlFormatKeywords
{
    /// <summary>Token types to uppercase. Built once — the type numbers are compile-time constants of the
    /// generated lexer, so this is a set lookup per token and no string work at all.</summary>
    public static readonly HashSet<int> Uppercased =
    [
        // Statement and clause spine.
        PostgreSQLLexer.SELECT, PostgreSQLLexer.FROM, PostgreSQLLexer.WHERE,
        PostgreSQLLexer.GROUP_P, PostgreSQLLexer.BY, PostgreSQLLexer.HAVING,
        PostgreSQLLexer.ORDER, PostgreSQLLexer.WINDOW, PostgreSQLLexer.LIMIT,
        PostgreSQLLexer.OFFSET, PostgreSQLLexer.FETCH, PostgreSQLLexer.ONLY,
        PostgreSQLLexer.WITH, PostgreSQLLexer.RECURSIVE, PostgreSQLLexer.MATERIALIZED,
        PostgreSQLLexer.UNION, PostgreSQLLexer.EXCEPT, PostgreSQLLexer.INTERSECT,
        PostgreSQLLexer.INSERT, PostgreSQLLexer.INTO, PostgreSQLLexer.VALUES,
        PostgreSQLLexer.UPDATE, PostgreSQLLexer.SET, PostgreSQLLexer.DELETE_P,
        PostgreSQLLexer.RETURNING, PostgreSQLLexer.CONFLICT, PostgreSQLLexer.DO,
        PostgreSQLLexer.NOTHING, PostgreSQLLexer.DEFAULT,

        // Joins.
        PostgreSQLLexer.JOIN, PostgreSQLLexer.LEFT, PostgreSQLLexer.RIGHT,
        PostgreSQLLexer.INNER_P, PostgreSQLLexer.OUTER_P, PostgreSQLLexer.FULL,
        PostgreSQLLexer.CROSS, PostgreSQLLexer.NATURAL, PostgreSQLLexer.ON,
        PostgreSQLLexer.USING, PostgreSQLLexer.LATERAL_P,

        // Predicates and expression keywords.
        PostgreSQLLexer.AND, PostgreSQLLexer.OR, PostgreSQLLexer.NOT,
        PostgreSQLLexer.IN_P, PostgreSQLLexer.EXISTS, PostgreSQLLexer.BETWEEN,
        PostgreSQLLexer.LIKE, PostgreSQLLexer.ILIKE, PostgreSQLLexer.SIMILAR,
        PostgreSQLLexer.IS, PostgreSQLLexer.ISNULL, PostgreSQLLexer.NOTNULL,
        PostgreSQLLexer.NULL_P, PostgreSQLLexer.TRUE_P, PostgreSQLLexer.FALSE_P,
        PostgreSQLLexer.CASE, PostgreSQLLexer.WHEN, PostgreSQLLexer.THEN,
        PostgreSQLLexer.ELSE, PostgreSQLLexer.END_P, PostgreSQLLexer.CAST,
        PostgreSQLLexer.AS, PostgreSQLLexer.DISTINCT, PostgreSQLLexer.ALL,
        PostgreSQLLexer.ANY, PostgreSQLLexer.SOME, PostgreSQLLexer.ASC,
        PostgreSQLLexer.DESC, PostgreSQLLexer.NULLS_P, PostgreSQLLexer.FIRST_P,
        PostgreSQLLexer.LAST_P, PostgreSQLLexer.OVER, PostgreSQLLexer.PARTITION,
        PostgreSQLLexer.FILTER,
    ];
}
