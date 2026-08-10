using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Bearing.App.Formatting;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Sql;

namespace Bearing.App.Results;

/// <summary>
/// Pure inline-edit logic: turns a result set's pending state (edits/inserts/deletes) into
/// parameterized <see cref="SqlWriteCommand"/>s, applies a successful save back into the grid rows,
/// and renders values as SQL literals for preview. Also builds the FK-navigation lookup. No
/// connection or UI state — extracted from the shell view-model as a named, testable unit.
/// </summary>
internal static class ResultEditModel
{
    public enum ChangeKind { Delete, Update, Insert }

    /// <summary>A pending change tagged with the grid row it came from, so the saved result can be
    /// applied back to that exact row (delete → remove, update → committed values, insert → RETURNING).</summary>
    public sealed record PendingChange(ChangeKind Kind, object?[] Row, SqlWriteCommand Command);

    /// <summary>Turn a result set's pending state into row-tagged, ordered changes (deletes, updates, inserts).</summary>
    public static List<PendingChange> BuildPendingChanges(ResultSetViewModel rs, EditTarget t)
    {
        var changes = new List<PendingChange>();

        foreach (var row in rs.DeletedRows)
        {
            var keys = KeyValues(t, rs.OriginalOf(row) ?? row);
            if (keys.Count > 0) changes.Add(new PendingChange(ChangeKind.Delete, row, DmlGenerator.Delete(t.Schema, t.Table, keys)));
        }
        foreach (var row in rs.EditedRows)
        {
            if (rs.OriginalOf(row) is not { } original) continue;
            var assignments = ChangedAssignments(rs, t, original, row);
            var keys = KeyValues(t, original);
            if (assignments.Count > 0 && keys.Count > 0)
                changes.Add(new PendingChange(ChangeKind.Update, row, DmlGenerator.Update(t.Schema, t.Table, assignments, keys)));
        }
        foreach (var row in rs.NewRows)
        {
            var values = InsertValues(rs, t, row);
            if (values.Count > 0) changes.Add(new PendingChange(ChangeKind.Insert, row, DmlGenerator.Insert(t.Schema, t.Table, values)));
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
                    rs.ReplaceRow(ch.Row, CommittedRow(rs, target, ch.Row));
                    break;
                case ChangeKind.Insert:
                    var returned = i < results.Count ? MapReturnedRow(results[i], rs.Columns) : null;
                    rs.ReplaceRow(ch.Row, returned ?? CommittedRow(rs, target, ch.Row));
                    break;
            }
        }
        rs.ClearPending();
    }

    /// <summary>Substitute a command's @pN parameters with SQL literals in a single pass (so neither
    /// overlapping names nor a value that contains "@pN" corrupts the rendered SQL).</summary>
    public static string InlineParameters(SqlWriteCommand c)
    {
        var byName = c.Parameters.ToDictionary(p => p.Name, p => p.Value);
        return Regex.Replace(c.Sql, @"@p\d+", m =>
            byName.TryGetValue(m.Value, out var v) ? (v is null ? "null" : SqlLiteral(v)) : m.Value);
    }

    /// <summary>`select * from ref where refcol = &lt;value&gt; [and …]` with all key parts from the row.</summary>
    public static string BuildForeignKeySelect(ForeignKeyTarget t, object?[] row)
    {
        var preds = new List<string>(t.RefColumns.Count);
        for (var i = 0; i < t.RefColumns.Count; i++)
        {
            var value = row[t.SourceColumnIndices[i]];
            preds.Add(value is null
                ? $"{QuoteIdent(t.RefColumns[i])} is null"
                : $"{QuoteIdent(t.RefColumns[i])} = {SqlLiteral(value)}");
        }
        return $"select * from {QuoteIdent(t.RefSchema)}.{QuoteIdent(t.RefTable)}\nwhere {string.Join("\n  and ", preds)};";
    }

    /// <summary>The committed form of an edited row: original values with the edited cells coerced to
    /// their column type (so the grid shows canonical values after save).</summary>
    private static object?[] CommittedRow(ResultSetViewModel rs, EditTarget t, object?[] row)
    {
        var committed = (object?[])row.Clone();
        foreach (var c in t.Columns)
            if (c.ResultIndex < committed.Length && committed[c.ResultIndex] is string s)
                committed[c.ResultIndex] = Coerce(s, rs.Columns[c.ResultIndex].ClrType);
        return committed;
    }

    /// <summary>Build a result-shaped row from an INSERT … RETURNING result, matching columns by name.</summary>
    private static object?[]? MapReturnedRow(QueryResult res, IReadOnlyList<ColumnDescriptor> resultColumns)
    {
        if (!res.Success || res.Columns.Count == 0 || res.Rows.Count == 0) return null;
        var byName = new Dictionary<string, int>();
        for (var j = 0; j < res.Columns.Count; j++) byName[res.Columns[j].Name] = j;

        var row = new object?[resultColumns.Count];
        for (var k = 0; k < resultColumns.Count; k++)
            row[k] = byName.TryGetValue(resultColumns[k].Name, out var j) ? res.Rows[0][j] : null;
        return row;
    }

    /// <summary>Primary-key predicates from the row's original (typed) values.</summary>
    private static List<ColumnValue> KeyValues(EditTarget t, object?[] source)
        => t.KeyColumns
            .Where(k => k.ResultIndex < source.Length)
            .Select(k => new ColumnValue(k.BaseColumn, source[k.ResultIndex]))
            .ToList();

    /// <summary>Assignments for columns whose value differs from the original (coerced to the column type).</summary>
    private static List<ColumnValue> ChangedAssignments(ResultSetViewModel rs, EditTarget t, object?[] original, object?[] row)
    {
        var list = new List<ColumnValue>();
        foreach (var c in t.Columns)
        {
            if (c.ResultIndex >= row.Length || c.ResultIndex >= original.Length) continue;
            // Compare *coerced* to original: the grid writes strings, so a cell holding "5" never equalled the
            // typed 5 it came from and every touched cell produced an assignment — re-writing values that
            // hadn't changed (and re-touching audit triggers on those columns).
            var value = Coerce(row[c.ResultIndex], rs.Columns[c.ResultIndex].ClrType);
            if (Equals(value, original[c.ResultIndex])) continue;
            list.Add(new ColumnValue(c.BaseColumn, value));
        }
        return list;
    }

    /// <summary>Insert values for the user-filled (non-null) columns; null cells are left to DB defaults.</summary>
    private static List<ColumnValue> InsertValues(ResultSetViewModel rs, EditTarget t, object?[] row)
    {
        var list = new List<ColumnValue>();
        foreach (var c in t.Columns)
        {
            if (c.ResultIndex >= row.Length) continue;
            var value = row[c.ResultIndex];
            if (value is null) continue; // let serial/defaults fill it
            list.Add(new ColumnValue(c.BaseColumn, Coerce(value, rs.Columns[c.ResultIndex].ClrType)));
        }
        return list;
    }

    /// <summary>Coerce a grid string back to the column's CLR type. The "(null)" token ⇒ NULL; an empty
    /// string stays empty for text columns and ⇒ NULL for others. Falls back to the raw string (letting
    /// the DB reject it) when parsing fails.</summary>
    private static object? Coerce(object? value, Type clrType)
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
            // Dates: accept the display pattern (dd.MM.yyyy HH:mm:ss) the user sees, else a lenient parse.
            if (CellFormat.TryParseDate(s, t, out var date)) return date;
            return Convert.ChangeType(s, t, CultureInfo.InvariantCulture);
        }
        catch { return s; }
    }

    private static string QuoteIdent(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";

    /// <summary>Format a key value as a SQL literal. Values come from the DB (not user text); strings
    /// and other types are single-quoted (with '' escaping) and left to Postgres to cast.</summary>
    private static string SqlLiteral(object value) => value switch
    {
        bool b => b ? "true" : "false",
        byte or sbyte or short or ushort or int or uint or long or ulong
            => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        _ => "'" + value.ToString()!.Replace("'", "''") + "'",
    };
}
