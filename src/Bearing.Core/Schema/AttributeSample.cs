using System.Collections.Generic;

namespace Bearing.Core.Schema;

/// <summary>
/// What one small read of a table's items found, for an engine that declares no columns beyond its keys
/// (DynamoDB). A <b>sample</b>, never the table's schema: other items may carry other attributes, and the tree
/// says so beside it.
/// <para>
/// Absences stay typed (§1.1). A sample that could not be read carries the reason in <see cref="Refused"/> and an
/// empty <see cref="Attributes"/> — never an empty list standing in for "this table has no other attributes".
/// A sample that was read and found none has a null <see cref="Refused"/> and says how many items it read.
/// </para>
/// </summary>
/// <param name="Attributes">The attributes seen, the declared ones (the keys) left out, ordinal by name.</param>
/// <param name="ItemsRead">How many items the read returned — the size of the sample.</param>
/// <param name="Refused">Why there is no sample (scrubbed), or null when there is one.</param>
public sealed record AttributeSample(IReadOnlyList<SampledAttribute> Attributes, int ItemsRead, string? Refused = null)
{
    /// <summary>A sample that could not be read, and why.</summary>
    public static AttributeSample RefusedBecause(string reason) => new([], 0, reason);
}

/// <summary>One attribute seen in a sample, with the type its values had — <c>mixed</c> when they differed.</summary>
public sealed record SampledAttribute(string Name, string TypeName);
