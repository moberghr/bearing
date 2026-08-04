namespace Bearing.Core.Data;

/// <summary>A column name paired with a value — an assignment, a key predicate, or an insert value.</summary>
public sealed record ColumnValue(string Column, object? Value);

/// <summary>A parameterized value; the provider maps <see cref="Name"/>/<see cref="Value"/> to its
/// own parameter type when executing. Keeping this provider-agnostic lets the generator stay unit-testable.</summary>
public sealed record SqlParameter(string Name, object? Value);

/// <summary>A generated, fully-parameterized write (UPDATE/DELETE/INSERT) ready to execute.</summary>
public sealed record SqlWriteCommand(string Sql, IReadOnlyList<SqlParameter> Parameters);
