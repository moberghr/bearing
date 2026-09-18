using System.Linq;
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
/// <para>
/// The one exception to "every value is a parameter" is a <see cref="SqlExpression"/>, emitted where its
/// parameter would have gone (#149). It reaches here only from a dialect's own fixed table of canonical
/// spellings (<see cref="ISqlDialect.TryEditExpression"/>), so what is interpolated is never a string the
/// user typed — see <see cref="EditExpression"/> for why the exception is bounded. A key predicate refuses
/// one outright: a <c>WHERE</c> built from anything but the row's own stored values would be a write aimed
/// at rows nobody picked.
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
        => Update(PostgresDialect.Instance, schema, table, assignments, keys, returning);

    /// <inheritdoc cref="Update(string?, string, IReadOnlyList{ColumnValue}, IReadOnlyList{ColumnValue}, IReadOnlyList{string})"/>
    public static SqlWriteCommand Update(
        ISqlDialect dialect, string? schema, string table,
        IReadOnlyList<ColumnValue> assignments, IReadOnlyList<ColumnValue> keys,
        IReadOnlyList<string>? returning = null)
    {
        if (assignments.Count == 0) throw new ArgumentException("UPDATE needs at least one assignment.", nameof(assignments));
        if (keys.Count == 0) throw new ArgumentException("UPDATE needs at least one key column.", nameof(keys));

        var ps = new List<SqlParameter>();
        var sets = new List<string>(assignments.Count);
        foreach (var a in assignments)
            sets.Add($"{Ident(dialect, a.Column)} = {AddParam(ps, a.Value)}");

        var where = BuildWhere(dialect, keys, ps);
        var qualified = Qualify(dialect, schema, table);
        var setList = string.Join(", ", sets);
        var readBack = returning is { Count: > 0 }
            ? returning.Select(c => Ident(dialect, c)).ToList()
            : null;

        return new SqlWriteCommand(
            dialect.UpdateStatement(qualified, setList, where, readBack),
            ps,
            // The same statement with nothing read back, for an executor the clause is refused (SQL Server
            // on a table with an enabled trigger — Msg 334 covers UPDATE as it does INSERT). Only when
            // there was a clause to strip; the parameters are identical, so the retry reuses this list.
            readBack is null ? null : dialect.UpdateStatement(qualified, setList, where, null));
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
        {
            // A key comes from the row's stored original values and can never be user text, so this is an
            // invariant check rather than validation — but it is the one place where letting SQL through
            // would aim the write at other rows, so it is stated rather than assumed.
            if (k.Value is SqlExpression)
                throw new ArgumentException($"Key column '{k.Column}' cannot be an expression.", nameof(keys));
            preds.Add(k.Value is null
                ? $"{Ident(dialect, k.Column)} is null"
                : $"{Ident(dialect, k.Column)} = {AddParam(ps, k.Value)}");
        }
        return string.Join(" and ", preds);
    }

    /// <summary>The slot a value occupies in the statement: a fresh parameter (`@p0`, `@p1`, … — the one
    /// placeholder spelling both Npgsql and SqlClient accept), or the expression itself.</summary>
    private static string AddParam(List<SqlParameter> ps, object? value)
    {
        if (value is SqlExpression e) return e.Sql;
        var name = "@p" + ps.Count;
        ps.Add(new SqlParameter(name, value));
        return name;
    }

    /// <summary>Generated DML always quotes — nobody types over this output, so the safe form wins.</summary>
    private static string Ident(ISqlDialect dialect, string id) => dialect.Quote(id);

    private static string Qualify(ISqlDialect dialect, string? schema, string table) =>
        string.IsNullOrEmpty(schema) ? Ident(dialect, table) : $"{Ident(dialect, schema)}.{Ident(dialect, table)}";
}
