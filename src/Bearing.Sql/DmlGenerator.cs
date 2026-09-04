using Bearing.Core.Data;

namespace Bearing.Sql;

/// <summary>
/// Builds parameterized UPDATE/DELETE/INSERT statements from a table + column values. Pure: every value
/// is a parameter, never interpolated, and every identifier is quoted by the dialect. The executor binds
/// the returned <see cref="SqlParameter"/>s to its driver.
/// <para>
/// The dialect-less overloads generate Postgres, which is what every caller wanted when there was one
/// engine. A caller that holds a connection must pass that connection's dialect — the quoting and the
/// "give me back the row I just wrote" clause both change with it (§5.4).
/// </para>
/// </summary>
public static class DmlGenerator
{
    /// <summary>`update t set a=@p… where k=@p…` from edited cells + the row's original key values.</summary>
    public static SqlWriteCommand Update(
        string? schema, string table,
        IReadOnlyList<ColumnValue> assignments, IReadOnlyList<ColumnValue> keys)
        => Update(PostgresDialect.Instance, schema, table, assignments, keys);

    /// <inheritdoc cref="Update(string?, string, IReadOnlyList{ColumnValue}, IReadOnlyList{ColumnValue})"/>
    public static SqlWriteCommand Update(
        ISqlDialect dialect, string? schema, string table,
        IReadOnlyList<ColumnValue> assignments, IReadOnlyList<ColumnValue> keys)
    {
        if (assignments.Count == 0) throw new ArgumentException("UPDATE needs at least one assignment.", nameof(assignments));
        if (keys.Count == 0) throw new ArgumentException("UPDATE needs at least one key column.", nameof(keys));

        var ps = new List<SqlParameter>();
        var sets = new List<string>(assignments.Count);
        foreach (var a in assignments)
            sets.Add($"{Ident(dialect, a.Column)} = {AddParam(ps, a.Value)}");

        var where = BuildWhere(dialect, keys, ps);
        return new SqlWriteCommand(
            $"update {Qualify(dialect, schema, table)} set {string.Join(", ", sets)} where {where}", ps);
    }

    /// <summary>`delete from t where k=@p…` keyed by the row's primary key.</summary>
    public static SqlWriteCommand Delete(string? schema, string table, IReadOnlyList<ColumnValue> keys)
        => Delete(PostgresDialect.Instance, schema, table, keys);

    /// <inheritdoc cref="Delete(string?, string, IReadOnlyList{ColumnValue})"/>
    public static SqlWriteCommand Delete(
        ISqlDialect dialect, string? schema, string table, IReadOnlyList<ColumnValue> keys)
    {
        if (keys.Count == 0) throw new ArgumentException("DELETE needs at least one key column.", nameof(keys));

        var ps = new List<SqlParameter>();
        var where = BuildWhere(dialect, keys, ps);
        return new SqlWriteCommand($"delete from {Qualify(dialect, schema, table)} where {where}", ps);
    }

    /// <summary>`insert into t (cols) values (@p…) returning *` — the returning clause refills generated
    /// keys and defaults. Where that clause goes is the dialect's call
    /// (<see cref="ISqlDialect.InsertStatement"/>): T-SQL puts it before VALUES.</summary>
    public static SqlWriteCommand Insert(string? schema, string table, IReadOnlyList<ColumnValue> values)
        => Insert(PostgresDialect.Instance, schema, table, values);

    /// <inheritdoc cref="Insert(string?, string, IReadOnlyList{ColumnValue})"/>
    public static SqlWriteCommand Insert(
        ISqlDialect dialect, string? schema, string table, IReadOnlyList<ColumnValue> values)
    {
        if (values.Count == 0) throw new ArgumentException("INSERT needs at least one column.", nameof(values));

        var ps = new List<SqlParameter>();
        var cols = new List<string>(values.Count);
        var slots = new List<string>(values.Count);
        foreach (var v in values)
        {
            cols.Add(Ident(dialect, v.Column));
            slots.Add(AddParam(ps, v.Value));
        }
        var qualified = Qualify(dialect, schema, table);
        var columnList = string.Join(", ", cols);
        var valueList = string.Join(", ", slots);
        return new SqlWriteCommand(
            dialect.InsertStatement(qualified, columnList, valueList, withReturning: true),
            ps,
            // The same insert with nothing returned, for an executor that is refused the returning clause
            // (SQL Server on a table with an enabled trigger). The parameters are identical, so the retry
            // reuses this list verbatim.
            dialect.InsertStatement(qualified, columnList, valueList, withReturning: false));
    }

    private static string BuildWhere(ISqlDialect dialect, IReadOnlyList<ColumnValue> keys, List<SqlParameter> ps)
    {
        var preds = new List<string>(keys.Count);
        foreach (var k in keys)
            preds.Add(k.Value is null
                ? $"{Ident(dialect, k.Column)} is null"
                : $"{Ident(dialect, k.Column)} = {AddParam(ps, k.Value)}");
        return string.Join(" and ", preds);
    }

    /// <summary>`@p0`, `@p1`, … — the one placeholder spelling both Npgsql and SqlClient accept.</summary>
    private static string AddParam(List<SqlParameter> ps, object? value)
    {
        var name = "@p" + ps.Count;
        ps.Add(new SqlParameter(name, value));
        return name;
    }

    /// <summary>Generated DML always quotes — nobody types over this output, so the safe form wins.</summary>
    private static string Ident(ISqlDialect dialect, string id) => dialect.Quote(id);

    private static string Qualify(ISqlDialect dialect, string? schema, string table) =>
        string.IsNullOrEmpty(schema) ? Ident(dialect, table) : $"{Ident(dialect, schema)}.{Ident(dialect, table)}";
}
