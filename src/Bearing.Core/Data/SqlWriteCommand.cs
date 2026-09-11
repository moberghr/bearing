namespace Bearing.Core.Data;

/// <summary>A column name paired with a value — an assignment, a key predicate, or an insert value.</summary>
public sealed record ColumnValue(string Column, object? Value);

/// <summary>A parameterized value; the provider maps <see cref="Name"/>/<see cref="Value"/> to its
/// own parameter type when executing. Keeping this provider-agnostic lets the generator stay unit-testable.</summary>
public sealed record SqlParameter(string Name, object? Value);

/// <summary>
/// A generated, fully-parameterized write (UPDATE/DELETE/INSERT) ready to execute.
/// <para>
/// <see cref="SqlWithoutReturning"/> is the same statement with the clause that returns the written row
/// removed, and it is set only where such a clause was added. It exists because SQL Server refuses
/// <c>OUTPUT</c> without an <c>INTO</c> target outright on a table with an enabled trigger (Msg 334) —
/// a routine shape for an audited table — which would otherwise make a grid insert impossible rather
/// than merely unable to refill. An executor that meets that refusal re-runs the batch from this text
/// and returns an affected-rows result instead of the row; the caller already falls back to the values
/// the user typed. Null means there was nothing to strip, so there is nothing to retry with.
/// </para>
/// </summary>
public sealed record SqlWriteCommand(
    string Sql,
    IReadOnlyList<SqlParameter> Parameters,
    string? SqlWithoutReturning = null);
