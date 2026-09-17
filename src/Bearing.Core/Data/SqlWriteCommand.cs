namespace Bearing.Core.Data;

/// <summary>A column name paired with a value — an assignment, a key predicate, or an insert value.</summary>
public sealed record ColumnValue(string Column, object? Value);

/// <summary>
/// A <see cref="ColumnValue.Value"/> that is SQL to be evaluated by the server, not a value to be sent to it
/// (#149) — <c>now()</c>, <c>default</c>, <c>gen_random_uuid()</c>. The generator emits <see cref="Sql"/>
/// verbatim where a parameter would otherwise go.
/// <para>
/// It is a type rather than a flag on the value so that reaching this path is a deliberate act with a name:
/// every other value in a generated write is a parameter (§5.4), and a bool beside a boxed object would make
/// the exception to that rule easy to set by accident. The only producer is
/// <c>Bearing.Sql.EditExpression</c>, whose canonical spellings are the only strings that ever get here.
/// </para>
/// </summary>
public sealed record SqlExpression(string Sql);

/// <summary>A parameterized value; the provider maps <see cref="Name"/>/<see cref="Value"/> to its
/// own parameter type when executing. Keeping this provider-agnostic lets the generator stay unit-testable.</summary>
public sealed record SqlParameter(string Name, object? Value);

/// <summary>A generated, fully-parameterized write (UPDATE/DELETE/INSERT) ready to execute.</summary>
public sealed record SqlWriteCommand(string Sql, IReadOnlyList<SqlParameter> Parameters);
