using Squirrel.Core.Data;

namespace Squirrel.Sql;

/// <summary>
/// Builds parameterized UPDATE/DELETE/INSERT statements from a table + column values. Pure and
/// engine-agnostic: identifiers are double-quoted (Postgres-style) and every value is a parameter,
/// never interpolated. The executor binds the returned <see cref="SqlParameter"/>s to its driver.
/// </summary>
public static class DmlGenerator
{
    /// <summary>`update t set a=@p… where k=@p…` from edited cells + the row's original key values.</summary>
    public static SqlWriteCommand Update(
        string? schema, string table,
        IReadOnlyList<ColumnValue> assignments, IReadOnlyList<ColumnValue> keys)
    {
        if (assignments.Count == 0) throw new ArgumentException("UPDATE needs at least one assignment.", nameof(assignments));
        if (keys.Count == 0) throw new ArgumentException("UPDATE needs at least one key column.", nameof(keys));

        var ps = new List<SqlParameter>();
        var sets = new List<string>(assignments.Count);
        foreach (var a in assignments)
            sets.Add($"{Ident(a.Column)} = {AddParam(ps, a.Value)}");

        var where = BuildWhere(keys, ps);
        return new SqlWriteCommand($"update {Qualify(schema, table)} set {string.Join(", ", sets)} where {where}", ps);
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
            preds.Add(k.Value is null
                ? $"{Ident(k.Column)} is null"
                : $"{Ident(k.Column)} = {AddParam(ps, k.Value)}");
        return string.Join(" and ", preds);
    }

    private static string AddParam(List<SqlParameter> ps, object? value)
    {
        var name = "@p" + ps.Count;
        ps.Add(new SqlParameter(name, value));
        return name;
    }

    private static string Ident(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";

    private static string Qualify(string? schema, string table) =>
        string.IsNullOrEmpty(schema) ? Ident(table) : $"{Ident(schema)}.{Ident(table)}";
}
