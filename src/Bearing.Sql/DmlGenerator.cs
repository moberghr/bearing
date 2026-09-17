using System.Linq;
using Bearing.Core.Data;

namespace Bearing.Sql;

/// <summary>
/// Builds parameterized UPDATE/DELETE/INSERT statements from a table + column values. Pure and
/// engine-agnostic: identifiers are double-quoted (Postgres-style) and every value is a parameter,
/// never interpolated. The executor binds the returned <see cref="SqlParameter"/>s to its driver.
/// <para>
/// The one exception is a <see cref="SqlExpression"/>, which is emitted where its parameter would have gone
/// (#149). It reaches here only from <see cref="EditExpression"/>'s fixed table of canonical spellings, so
/// what is interpolated is never a string the user typed — see that class for why the exception is bounded.
/// A key predicate refuses one outright: a <c>WHERE</c> built from anything but the row's own stored values
/// would be a write aimed at rows nobody picked.
/// </para>
/// </summary>
public static class DmlGenerator
{
    /// <summary>`update t set a=@p… where k=@p… [returning cols]` from edited cells + the row's original
    /// key values.</summary>
    /// <param name="returning">
    /// The columns to read back, so the grid can show what actually landed rather than what was typed — an
    /// expression's value is only known on the server, and so is a trigger's or an <c>on update</c> default's.
    /// Empty omits the clause.
    /// <para>
    /// Named columns rather than <c>*</c> on purpose: these are the base columns of the result already on
    /// screen, so the user demonstrably has <c>SELECT</c> on them. Postgres grants privileges per column, and
    /// a <c>returning *</c> would make a save fail on a table the user can update but cannot read in full.
    /// </para>
    /// </param>
    public static SqlWriteCommand Update(
        string? schema, string table,
        IReadOnlyList<ColumnValue> assignments, IReadOnlyList<ColumnValue> keys,
        IReadOnlyList<string>? returning = null)
    {
        if (assignments.Count == 0) throw new ArgumentException("UPDATE needs at least one assignment.", nameof(assignments));
        if (keys.Count == 0) throw new ArgumentException("UPDATE needs at least one key column.", nameof(keys));

        var ps = new List<SqlParameter>();
        var sets = new List<string>(assignments.Count);
        foreach (var a in assignments)
            sets.Add($"{Ident(a.Column)} = {AddParam(ps, a.Value)}");

        var where = BuildWhere(keys, ps);
        var tail = returning is { Count: > 0 }
            ? " returning " + string.Join(", ", returning.Select(Ident))
            : "";
        return new SqlWriteCommand(
            $"update {Qualify(schema, table)} set {string.Join(", ", sets)} where {where}{tail}", ps);
    }

    /// <summary>`delete from t where k=@p…` keyed by the row's primary key.</summary>
    public static SqlWriteCommand Delete(string? schema, string table, IReadOnlyList<ColumnValue> keys)
    {
        if (keys.Count == 0) throw new ArgumentException("DELETE needs at least one key column.", nameof(keys));

        var ps = new List<SqlParameter>();
        var where = BuildWhere(keys, ps);
        return new SqlWriteCommand($"delete from {Qualify(schema, table)} where {where}", ps);
    }

    /// <summary>`insert into t (cols) values (@p…) returning *` — RETURNING refills generated keys/defaults.</summary>
    public static SqlWriteCommand Insert(string? schema, string table, IReadOnlyList<ColumnValue> values)
    {
        if (values.Count == 0) throw new ArgumentException("INSERT needs at least one column.", nameof(values));

        var ps = new List<SqlParameter>();
        var cols = new List<string>(values.Count);
        var slots = new List<string>(values.Count);
        foreach (var v in values)
        {
            cols.Add(Ident(v.Column));
            slots.Add(AddParam(ps, v.Value));
        }
        return new SqlWriteCommand(
            $"insert into {Qualify(schema, table)} ({string.Join(", ", cols)}) values ({string.Join(", ", slots)}) returning *", ps);
    }

    private static string BuildWhere(IReadOnlyList<ColumnValue> keys, List<SqlParameter> ps)
    {
        var preds = new List<string>(keys.Count);
        foreach (var k in keys)
        {
            // A key comes from the row's stored original values and can never be user text, so this is an
            // invariant check rather than validation — but it is the one place where letting SQL through
            // would aim the write at other rows, so it is stated rather than assumed.
            if (k.Value is SqlExpression)
                throw new ArgumentException($"Key column '{k.Column}' cannot be an expression.", nameof(keys));
            preds.Add(k.Value is null
                ? $"{Ident(k.Column)} is null"
                : $"{Ident(k.Column)} = {AddParam(ps, k.Value)}");
        }
        return string.Join(" and ", preds);
    }

    /// <summary>The slot a value occupies in the statement: a fresh parameter, or the expression itself.</summary>
    private static string AddParam(List<SqlParameter> ps, object? value)
    {
        if (value is SqlExpression e) return e.Sql;
        var name = "@p" + ps.Count;
        ps.Add(new SqlParameter(name, value));
        return name;
    }

    /// <summary>Generated DML always quotes — nobody types over this output, so the safe form wins.</summary>
    private static string Ident(string id) => PgIdentifier.Quote(id);

    private static string Qualify(string? schema, string table) =>
        string.IsNullOrEmpty(schema) ? Ident(table) : $"{Ident(schema)}.{Ident(table)}";
}
