using Bearing.Core.Schema;

namespace Bearing.Core.Completion;

/// <summary>
/// A FROM/JOIN source in the query under the caret, after resolving against the schema snapshot.
/// Replaces the prototype's SourceRowset.
/// </summary>
public sealed class TableRef
{
    public string? Schema { get; init; }

    /// <summary>The relation name exactly as typed.</summary>
    public required string RawName { get; init; }

    public string? Alias { get; init; }

    /// <summary>The resolved catalog relation, or null if it wasn't found in the snapshot.</summary>
    public TableInfo? Resolved { get; init; }

    /// <summary>How this source is referred to in the query: its alias if present, else its name.</summary>
    public string EffectiveName => Alias ?? RawName;
}
