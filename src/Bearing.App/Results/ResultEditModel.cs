using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Bearing.App.Connections;
using Bearing.App.Formatting;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Results;
using Bearing.Sessions;
using Bearing.Sql;

namespace Bearing.App.Results;

/// <summary>
/// Pure inline-edit logic: turns a result set's pending state (edits/inserts/deletes) into
/// parameterized <see cref="SqlWriteCommand"/>s, applies a successful save back into the grid rows,
/// and renders values as SQL literals for preview. Also builds the FK-navigation lookup. No
/// connection or UI state — extracted from the shell view-model as a named, testable unit.
/// <para>
/// Every entry point comes in two forms: a <see cref="ProviderTraits"/>-first one, which a caller holding
/// a connection must use, and a plain one that renders PostgreSQL. The difference is not cosmetic — the
/// identifier delimiter, the INSERT's "give me back the row I wrote" clause and the boolean literal all
/// change with the engine — so the plain overloads exist only for the callers (and tests) that
/// legitimately have no connection to ask.
/// </para>
/// </summary>
internal static class ResultEditModel
{
    public enum ChangeKind { Delete, Update, Insert }

    /// <summary>A pending change tagged with the grid row it came from, so the saved result can be
    /// applied back to that exact row (delete → remove, update → committed values, insert → RETURNING).</summary>
    public sealed record PendingChange(ChangeKind Kind, object?[] Row, SqlWriteCommand Command);

    /// <summary>Turn a result set's pending state into row-tagged, ordered changes (deletes, updates,
    /// inserts), as PostgreSQL.</summary>
    public static List<PendingChange> BuildPendingChanges(ResultSetViewModel rs, EditTarget t)
        => BuildPendingChanges(ProviderTraits.Postgres, rs, t);

    /// <inheritdoc cref="BuildPendingChanges(ResultSetViewModel, EditTarget)"/>
    public static List<PendingChange> BuildPendingChanges(
        ProviderTraits traits, ResultSetViewModel rs, EditTarget t)
    {
        var changes = new List<PendingChange>();
        var d = traits.Dialect;

        foreach (var row in rs.DeletedRows)
        {
            var keys = KeyValues(t, rs.OriginalOf(row) ?? row);
            if (keys.Count > 0) changes.Add(new PendingChange(ChangeKind.Delete, row, DmlGenerator.Delete(d, t.Schema, t.Table, keys)));
        }
        foreach (var row in rs.EditedRows)
        {
            if (rs.OriginalOf(row) is not { } original) continue;
            var assignments = ChangedAssignments(d, rs, t, original, row);
            var keys = KeyValues(t, original);
            if (assignments.Count > 0 && keys.Count > 0)
                changes.Add(new PendingChange(ChangeKind.Update, row,
                    DmlGenerator.Update(d, t.Schema, t.Table, assignments, keys, ReadBackColumns(t))));
        }
        foreach (var row in rs.NewRows)
        {
            var values = InsertValues(d, rs, t, row);
            if (values.Count > 0) changes.Add(new PendingChange(ChangeKind.Insert, row, DmlGenerator.Insert(d, t.Schema, t.Table, values)));
        }
        return changes;
    }

    /// <summary>Reflect a successful save back into the grid rows: remove deletes, swap updates for their
    /// committed values, swap new rows for the INSERT … RETURNING result.</summary>
    public static void ApplySavedChanges(
        ResultSetViewModel rs, EditTarget target, List<PendingChange> changes, IReadOnlyList<QueryResult> results)
    {
        for (var i = 0; i < changes.Count; i++)
        {
            var ch = changes[i];
            switch (ch.Kind)
            {
                case ChangeKind.Delete:
                    rs.RemoveRow(ch.Row);
                    break;
                case ChangeKind.Update:
                case ChangeKind.Insert:
                    // Both read back now (#149): an expression's value, a trigger's rewrite and an
                    // `on update` default are all only knowable from the server's answer. The locally
                    // committed row is the fallback for every column the statement did not return.
                    var committed = CommittedRow(rs, target, ch.Row);
                    rs.ReplaceRow(ch.Row, i < results.Count
                        ? ApplyReturnedRow(results[i], rs, target, committed)
                        : committed);
                    break;
            }
        }
        rs.ClearPending();
    }

    /// <summary>Substitute a command's @pN parameters with PostgreSQL literals in a single pass (so
    /// neither overlapping names nor a value that contains "@pN" corrupts the rendered SQL).</summary>
    public static string InlineParameters(SqlWriteCommand c)
        => InlineParameters(ProviderTraits.Postgres, c);

    /// <inheritdoc cref="InlineParameters(SqlWriteCommand)"/>
    public static string InlineParameters(ProviderTraits traits, SqlWriteCommand c)
    {
        var byName = c.Parameters.ToDictionary(p => p.Name, p => p.Value);
        return Regex.Replace(c.Sql, @"@p\d+", m =>
            byName.TryGetValue(m.Value, out var v) ? SqlValue.Literal(traits.Literals, v) : m.Value);
    }

    /// <summary>`select * from ref where refcol = &lt;value&gt; [and …]` with all key parts from the row.</summary>
    public static string BuildForeignKeySelect(ForeignKeyTarget t, object?[] row)
        => BuildForeignKeySelect(ProviderTraits.Postgres, t, row);

    /// <inheritdoc cref="BuildForeignKeySelect(ForeignKeyTarget, object?[])"/>
    /// <remarks>Unlike the inline-edit preview, this string is <em>executed</em>: a bracket-quoted
    /// identifier or a <c>1</c>/<c>0</c> boolean is the difference between a working lookup and a syntax
    /// error, so a caller with a connection has no business calling the plain overload.</remarks>
    public static string BuildForeignKeySelect(ProviderTraits traits, ForeignKeyTarget t, object?[] row)
    {
        var preds = new List<string>(t.RefColumns.Count);
        for (var i = 0; i < t.RefColumns.Count; i++)
        {
            var value = row[t.SourceColumnIndices[i]];
            preds.Add(value is null
                ? $"{QuoteIdent(traits, t.RefColumns[i])} is null"
                : $"{QuoteIdent(traits, t.RefColumns[i])} = {SqlValue.Literal(traits.Literals, value)}");
        }
        return $"select * from {QuoteIdent(traits, t.RefSchema)}.{QuoteIdent(traits, t.RefTable)}"
             + $"\nwhere {string.Join("\n  and ", preds)};";
    }

    /// <summary>The committed form of an edited row: original values with the edited cells coerced to
    /// their column type (so the grid shows canonical values after save).</summary>
    private static object?[] CommittedRow(ResultSetViewModel rs, EditTarget t, object?[] row)
    {
        var committed = (object?[])row.Clone();
        foreach (var c in t.Columns)
            if (c.ResultIndex < committed.Length && committed[c.ResultIndex] is string s)
                committed[c.ResultIndex] = Coerce(s, rs.Columns[c.ResultIndex].ClrType,
                    ColumnKinds.IsTimestampWithZone(rs.Columns[c.ResultIndex].DataTypeName));
        return committed;
    }

    /// <summary>
    /// Overlay a RETURNING result onto the locally committed row, matching each grid column to the returned
    /// one by its <b>base</b> column name and falling back to its displayed name.
    /// <para>
    /// The base name is what makes an aliased result survive: <c>select name as n</c> shows a column called
    /// <c>n</c> and gets back one called <c>name</c>, and matching on the displayed name alone would find
    /// nothing — and, in the version of this that built a fresh row, write a null over the value the user had
    /// just saved. A column the statement did not return keeps what the commit put there.
    /// </para>
    /// </summary>
    private static object?[] ApplyReturnedRow(
        QueryResult res, ResultSetViewModel rs, EditTarget t, object?[] committed)
    {
        if (!res.Success || res.Columns.Count == 0 || res.Rows.Count == 0) return committed;

        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var j = 0; j < res.Columns.Count; j++) byName[res.Columns[j].Name] = j;

        var baseNames = new Dictionary<int, string>();
        foreach (var c in t.Columns) baseNames[c.ResultIndex] = c.BaseColumn;

        var row = (object?[])committed.Clone();
        for (var k = 0; k < rs.Columns.Count && k < row.Length; k++)
        {
            var name = baseNames.TryGetValue(k, out var b) ? b : rs.Columns[k].Name;
            if (byName.TryGetValue(name, out var j) || byName.TryGetValue(rs.Columns[k].Name, out j))
                row[k] = res.Rows[0][j];
        }
        return row;
    }

    /// <summary>The base columns an UPDATE reads back — the ones this result already displays, so they are
    /// columns the user is known to be able to <c>SELECT</c> (see <see cref="DmlGenerator.Update"/>).</summary>
    private static IReadOnlyList<string> ReadBackColumns(EditTarget t)
        => t.Columns.Select(c => c.BaseColumn).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Primary-key predicates from the row's original (typed) values.</summary>
    private static List<ColumnValue> KeyValues(EditTarget t, object?[] source)
        => t.KeyColumns
            .Where(k => k.ResultIndex < source.Length)
            .Select(k => new ColumnValue(k.BaseColumn, source[k.ResultIndex]))
            .ToList();

    /// <summary>Assignments for columns whose value differs from the original (coerced to the column type).</summary>
    private static List<ColumnValue> ChangedAssignments(
        ISqlDialect dialect, ResultSetViewModel rs, EditTarget t, object?[] original, object?[] row)
    {
        var list = new List<ColumnValue>();
        foreach (var c in t.Columns)
        {
            if (c.ResultIndex >= row.Length || c.ResultIndex >= original.Length) continue;
            // Compare *coerced* to original: the grid writes strings, so a cell holding "5" never equalled the
            // typed 5 it came from and every touched cell produced an assignment — re-writing values that
            // hadn't changed (and re-touching audit triggers on those columns).
            var value = CoerceOrExpression(dialect, row[c.ResultIndex], rs.Columns[c.ResultIndex]);
            if (Equals(value, original[c.ResultIndex])) continue;
            list.Add(new ColumnValue(c.BaseColumn, value));
        }
        return list;
    }

    /// <summary>Insert values for the user-filled (non-null) columns; null cells are left to DB defaults.</summary>
    private static List<ColumnValue> InsertValues(
        ISqlDialect dialect, ResultSetViewModel rs, EditTarget t, object?[] row)
    {
        var list = new List<ColumnValue>();
        foreach (var c in t.Columns)
        {
            if (c.ResultIndex >= row.Length) continue;
            var value = row[c.ResultIndex];
            if (value is null) continue; // let serial/defaults fill it
            // The third Coerce call site, and it needs the zone flag as much as the other two: without it,
            // the same displayed wall time typed into a new row and into an existing one produced values
            // hours apart, and a Kind=Unspecified DateTime that Npgsql will not take for a timestamptz.
            list.Add(new ColumnValue(c.BaseColumn, CoerceOrExpression(dialect, value, rs.Columns[c.ResultIndex])));
        }
        return list;
    }

    /// <summary>
    /// Whether a pending cell value will be sent to the server as <b>raw text</b> despite the column not
    /// being a text one — that is, whether <see cref="Coerce"/> gave up on it and the write will fail.
    /// <para>
    /// It exists because digit grouping took away the only signal such a cell had. A numeric column draws
    /// <c>1,234</c> for 1234, and <c>CellFormat.TryParseNumber</c> refuses that exact string on purpose
    /// (<c>AllowThousands</c> is off — see #26) — so a user who types back what they just read gets a cell
    /// that is indistinguishable from a correct one and learns otherwise only when the batch fails.
    /// </para>
    /// <para>
    /// <b>The fix is emphatically not to accept the grouped form.</b> Under a comma-decimal locale
    /// <c>1,234</c> means 1.234, so reading it as 1234 would be a thousandfold write — the same class of bug
    /// as #26's tenfold one and larger. The ambiguity is real and unresolvable from the text, so the value
    /// stays refused and the cell says so instead.
    /// </para>
    /// </summary>
    /// <param name="dialect">The connection's dialect, because what counts as an expression rather than a
    /// refused value is engine-specific: <c>getdate()</c> is SQL on SQL Server and a typo on Postgres. The
    /// two questions have to be asked of the same table or a cell is drawn one way and written the other.</param>
    internal static bool WillReachServerAsText(ISqlDialect dialect, object? value, Type clrType)
    {
        if (value is not string s) return false;                          // already typed
        var t = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (t == typeof(string)) return false;                            // text column: raw text is correct
        if (s.Length == 0 || CellFormat.IsNullToken(s)) return false;     // both mean NULL, not a failure
        if (dialect.TryEditExpression(s) is not null) return false;        // SQL, and drawn as SQL (#149)
        return Coerce(s, t) is string;
    }

    /// <summary>
    /// The canonical SQL a pending cell value stands for, or null when it is an ordinary value (#149).
    /// <para>
    /// Both conditions are load-bearing. The column must be one the driver does <b>not</b> map to
    /// <see cref="string"/>, so a <c>text</c> cell holding <c>now()</c> stays the six characters the user
    /// typed. And <see cref="Coerce"/> must already have failed on it — a value that parses as the column's
    /// type is that value, and only a value that cannot be written at all is free to mean something else.
    /// Together they make this reinterpretation unable to change any edit that works today: the set it acts
    /// on is exactly the set <see cref="WillReachServerAsText"/> used to draw amber.
    /// </para>
    /// </summary>
    internal static string? ExpressionFor(ISqlDialect dialect, object? value, Type clrType)
    {
        if (value is not string s) return null;
        var t = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (t == typeof(string)) return null;                             // text column: text is the value
        if (s.Length == 0 || CellFormat.IsNullToken(s)) return null;       // both mean NULL
        if (dialect.TryEditExpression(s) is not { } sql) return null;
        return Coerce(s, t) is string ? sql : null;                        // a value that parses stays a value
    }

    /// <summary>Coerce a cell to its column's type, or — for the narrow case <see cref="ExpressionFor"/>
    /// describes — hand the generator SQL to emit in place of a parameter.</summary>
    private static object? CoerceOrExpression(ISqlDialect dialect, object? value, ColumnDescriptor column)
    {
        if (ExpressionFor(dialect, value, column.ClrType) is { } sql) return new SqlExpression(sql);
        return Coerce(value, column.ClrType, ColumnKinds.IsTimestampWithZone(column.DataTypeName));
    }

    /// <summary>Coerce a grid string back to the column's CLR type. The "(null)" token ⇒ NULL; an empty
    /// string stays empty for text columns and ⇒ NULL for others. Falls back to the raw string (letting
    /// the DB reject it) when parsing fails.
    /// <para>
    /// Internal rather than private because it is also the only sound answer to "will this pending edit
    /// reach the server as raw text" — the question <see cref="WillReachServerAsText"/> asks so the grid can
    /// mark the cell. A second predicate saying the same thing would drift from this one.
    /// </para></summary>
    /// <param name="utcColumn">
    /// True for a <c>timestamptz</c> column (#77). Its displayed text may have been converted into the
    /// display zone, so parsing it back has to undo that: a UTC 15:00 shown as 18:00+03:00 and edited must
    /// write back 15:00 UTC, not 18:00. Text without an offset is then a wall time in the zone the user was
    /// looking at, which is what they typed.
    /// </param>
    internal static object? Coerce(object? value, Type clrType, bool utcColumn = false)
    {
        if (value is not string s) return value; // unchanged cells keep their typed value
        if (CellFormat.IsNullToken(s)) return null;
        var t = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (s.Length == 0) return t == typeof(string) ? "" : null; // empty: keep for text, else NULL
        try
        {
            if (t == typeof(string)) return s;
            if (t == typeof(Guid)) return Guid.Parse(s);
            if (t == typeof(bool)) return bool.Parse(s);
            if (t.IsEnum) return Enum.Parse(t, s, ignoreCase: true);
            // Dates: accept the ISO display forms (yyyy-MM-dd HH:mm:ss) the user sees, else a lenient parse.
            if (CellFormat.TryParseDate(s, t, out var date, utcColumn, zone: null)) return date;
            // Numbers are CellFormat's, not Convert's. Convert.ChangeType allows group separators even under
            // InvariantCulture, so it reads "9,5" as 95 without complaint — a silent tenfold write for anyone
            // typing a comma-decimal. A refused number falls through to the raw string, which the server
            // rejects visibly. See CellFormat.TryParseNumber.
            if (CellFormat.IsNumeric(t)) return CellFormat.TryParseNumber(s, t, out var number) ? number : s;
            return Convert.ChangeType(s, t, CultureInfo.InvariantCulture);
        }
        catch { return s; }
    }

    /// <summary>Generated write SQL always quotes — nobody types over this output, so the safe form
    /// wins. The delimiter is the engine's: <c>"..."</c> for Postgres, <c>[...]</c> for T-SQL.</summary>
    private static string QuoteIdent(ProviderTraits traits, string ident) => traits.Dialect.Quote(ident);
}
