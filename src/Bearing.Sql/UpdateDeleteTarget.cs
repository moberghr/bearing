using System;
using System.Collections.Generic;
using Antlr4.Runtime;

namespace Bearing.Sql;

/// <summary>
/// A single-table <c>UPDATE</c> / <c>DELETE</c> reduced to the query that counts the rows it would touch
/// (#112) — the number that makes a wrong-tab mistake unmistakable in a way "are you sure" cannot.
/// </summary>
/// <param name="Verb"><c>UPDATE</c> or <c>DELETE</c>, uppercased.</param>
/// <param name="Relation">
/// The relation as the statement names it (<c>public.payment</c>, <c>"odd name"</c>) with any <c>ONLY</c>
/// and alias stripped — for display, so the prompt can say which table this lands in.
/// </param>
/// <param name="Target">
/// The whole target clause as written, alias and <c>ONLY</c> intact, so it can be dropped straight into a
/// <c>FROM</c>. Keeping the alias is not cosmetic: <c>update payment p … where p.amount &gt; 5</c> counts
/// only if the alias travels with it.
/// </param>
/// <param name="Where">The <c>WHERE</c> clause as written, or null when the statement has none — which is
/// #100's case, and is reported as "every row" rather than as a number.</param>
public sealed record UpdateDeleteTarget(string Verb, string Relation, string Target, string? Where)
{
    /// <summary>Whether this statement has no <c>WHERE</c> and therefore touches the whole table.</summary>
    public bool EveryRow => Where is null;

    /// <summary>
    /// The row-returning query whose count is the answer. Deliberately <c>select *</c> rather than
    /// <c>select count(*)</c>: <c>IQueryExecutor.CountAsync</c> wraps whatever it is handed in
    /// <c>select count(*) from (…)</c>, so counting here would count the count.
    /// </summary>
    public string RowsSql => Where is null ? $"select * from {Target}" : $"select * from {Target} where {Where}";

    /// <summary>
    /// Reduce one statement, or return null when it cannot be reduced <b>with certainty</b>.
    /// <para>
    /// Conservative by construction, and that asymmetry is the whole design: a missing number costs the user
    /// nothing (the confirmation reads as it did before #112), while a number counted from the wrong rows is
    /// worse than none at all — it is a reassurance about a statement nobody checked. So a multi-table
    /// <c>UPDATE … FROM</c> or <c>DELETE … USING</c>, a data-modifying CTE, a placeholder the count cannot
    /// bind, or anything else this lexer cannot flatten to one relation and one predicate declines.
    /// </para>
    /// <para>
    /// Lexer-based over <see cref="PgParsing"/>, like <see cref="WriteGuard"/> and
    /// <see cref="SqlRedactor"/> — a regex cannot tell a <c>where</c> inside a dollar-quoted body or a
    /// quoted identifier from the one that starts the predicate.
    /// </para>
    /// </summary>
    /// <param name="statement">One statement, as <see cref="WriteGuard.Describe"/> hands it over.</param>
    public static UpdateDeleteTarget? TryReduce(string statement)
    {
        if (string.IsNullOrWhiteSpace(statement)) return null;

        var tokens = OnChannel(statement);
        if (tokens.Count == 0) return null;

        var verb = tokens[0].Text.ToUpperInvariant();
        var isUpdate = verb == "UPDATE";
        if (!isUpdate && verb != "DELETE") return null;

        // A placeholder has no value here, so the count would either fail or — worse, if the driver defaulted
        // it — succeed against a different predicate than the statement will run.
        foreach (var token in tokens)
            if (IsPlaceholder(token.Text)) return null;

        var cursor = isUpdate ? 1 : 2;
        if (!isUpdate && !Is(tokens, 1, "FROM")) return null;   // `DELETE t` is not Postgres
        if (cursor >= tokens.Count) return null;

        // ---- the target clause -------------------------------------------------------------------------
        // Ends at the first top-level keyword that can follow it. SET is required for an UPDATE (anything
        // else means this is not the shape we think it is); a DELETE may end at WHERE, USING, RETURNING or
        // simply run out.
        var targetStart = cursor;
        var depth = 0;
        var targetEnd = -1;
        var terminator = "";
        for (var i = cursor; i < tokens.Count; i++)
        {
            depth += Nesting(tokens[i]);
            if (depth != 0) continue;

            // A comma at this level means several relations, and neither the count nor the display could
            // honestly pick one of them.
            if (tokens[i].Text == ",") return null;

            if (IsAny(tokens[i], "SET", "WHERE", "FROM", "USING", "RETURNING", ";"))
            {
                targetEnd = i - 1;
                terminator = tokens[i].Text.ToUpperInvariant();
                cursor = i + 1;
                break;
            }
        }
        if (targetEnd < 0) { targetEnd = tokens.Count - 1; cursor = tokens.Count; terminator = ""; }
        if (targetEnd < targetStart) return null;

        // An UPDATE must reach its SET list; a DELETE may only reach its predicate, its RETURNING or the end.
        // USING is rejected here rather than later because its own list contains a WHERE, and a predicate
        // read out of a join is a count of the wrong rows.
        if (isUpdate && terminator != "SET") return null;
        if (!isUpdate && terminator is not ("WHERE" or "RETURNING" or ";" or "")) return null;

        var target = Slice(statement, tokens[targetStart], tokens[targetEnd]);
        if (RelationName(tokens, targetStart, targetEnd) is not { } relation) return null;

        // ---- the predicate -----------------------------------------------------------------------------
        // For an UPDATE the SET list is skipped over; for a DELETE the terminator already landed us here.
        var where = terminator == "WHERE" ? cursor : -1;
        // Whether a WHERE keyword was seen at all, which is a different question from where the predicate
        // starts: for an UPDATE the target's terminator is SET, so the keyword is found by the scan below.
        var sawWhere = where >= 0;
        if (where < 0)
        {
            depth = 0;
            for (var i = cursor; i < tokens.Count; i++)
            {
                depth += Nesting(tokens[i]);
                if (depth != 0) continue;

                // A top-level FROM (update) or USING (delete) joins other relations in, so which rows match
                // is no longer a property of the target alone.
                if (IsAny(tokens[i], "FROM", "USING")) return null;
                if (Is(tokens, i, "WHERE")) { where = i + 1; sawWhere = true; break; }
                if (IsAny(tokens[i], "RETURNING", ";")) break;   // no predicate: every row
            }
        }

        // A WHERE keyword with nothing after it is not "every row" — it is a statement that will not run.
        // Saying "this will delete every row in t" about `delete from t where` states a fact the statement
        // does not have, so an unterminated predicate declines like every other shape this cannot flatten.
        // Keyed on having *seen* the keyword rather than on the terminator: an UPDATE's target ends at SET,
        // so its WHERE is found by the scan above and the terminator is no guide.
        if (sawWhere && where >= tokens.Count) return null;

        // No WHERE at all: every row (#100).
        if (where < 0 || where >= tokens.Count) return new UpdateDeleteTarget(verb, relation, target, null);

        // The predicate runs to a top-level RETURNING (which changes what comes back, not what matches) or
        // to the end of the statement.
        var whereEnd = tokens.Count - 1;
        depth = 0;
        for (var i = where; i < tokens.Count; i++)
        {
            depth += Nesting(tokens[i]);
            if (depth == 0 && IsAny(tokens[i], "RETURNING", ";")) { whereEnd = i - 1; break; }
        }
        if (whereEnd < where) return null;   // `where returning …` — not something to guess at

        return new UpdateDeleteTarget(verb, relation, target, Slice(statement, tokens[where], tokens[whereEnd]));
    }

    /// <summary>
    /// The relation name inside a target clause: <c>ONLY</c> dropped, the dotted identifier run taken, and
    /// any alias left behind. Null when the clause does not start with an identifier at all — a parenthesised
    /// or otherwise unexpected target, which is not something to name in a prompt.
    /// </summary>
    private static string? RelationName(IReadOnlyList<IToken> tokens, int start, int end)
    {
        var i = start;
        if (Is(tokens, i, "ONLY")) i++;
        if (i > end) return null;

        var name = "";
        while (i <= end)
        {
            var text = tokens[i].Text;
            if (name.Length == 0)
            {
                if (!IsIdentifierLike(text)) return null;
                name = text;
            }
            else if (text == ".")
            {
                if (i + 1 > end || !IsIdentifierLike(tokens[i + 1].Text)) return null;
                name += "." + tokens[i + 1].Text;
                i++;
            }
            else
            {
                break;   // an alias (with or without AS), or the legacy `*` suffix
            }
            i++;
        }
        return name.Length == 0 ? null : name;
    }

    /// <summary>Source text from one token through another, formatting and comments intact.</summary>
    private static string Slice(string statement, IToken first, IToken last)
        => statement.Substring(first.StartIndex, last.StopIndex - first.StartIndex + 1).Trim();

    private static List<IToken> OnChannel(string statement)
    {
        var list = new List<IToken>();
        foreach (var t in PgParsing.LexAll(statement))
        {
            if (t.Type == TokenConstants.EOF || t.Channel != TokenConstants.DefaultChannel) continue;
            if (t.Text is { Length: > 0 }) list.Add(t);
        }
        return list;
    }

    private static int Nesting(IToken token) => token.Type switch
    {
        PostgreSQLLexer.OPEN_PAREN => 1,
        PostgreSQLLexer.CLOSE_PAREN => -1,
        _ => 0,
    };

    private static bool Is(IReadOnlyList<IToken> tokens, int index, string keyword)
        => index >= 0 && index < tokens.Count
           && tokens[index].Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    private static bool IsAny(IToken token, params string[] keywords)
    {
        foreach (var keyword in keywords)
            if (token.Text.Equals(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>A bare word or a double-quoted identifier — not a literal, a number or an operator.</summary>
    private static bool IsIdentifierLike(string text)
    {
        if (text.Length == 0) return false;
        if (text[0] == '"') return true;
        return char.IsLetter(text[0]) || text[0] == '_';
    }

    /// <summary>A driver placeholder (<c>$1</c>) or a JDBC-style one (<c>?</c>), neither of which the count
    /// could bind.</summary>
    private static bool IsPlaceholder(string text)
    {
        if (text == "?") return true;
        if (text.Length < 2 || text[0] != '$') return false;
        for (var i = 1; i < text.Length; i++)
            if (!char.IsAsciiDigit(text[i])) return false;
        return true;
    }
}
