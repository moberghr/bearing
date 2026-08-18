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

    /// <summary>
    /// How the query itself writes this source's effective name — quotes included
    /// (<c>"__MigrationHistory"</c>). <see cref="Alias"/> / <see cref="RawName"/> are unquoted so
    /// matching stays simple, but emitted SQL has to qualify columns exactly the way the FROM clause
    /// spells the source, since Postgres folds an unquoted reference to lower case. Null when the
    /// source wasn't read from query text (hand-built refs in tests).
    /// </summary>
    public string? ReferenceText { get; init; }

    /// <summary>The resolved catalog relation, or null if it wasn't found in the snapshot.</summary>
    public TableInfo? Resolved { get; init; }

    /// <summary>How this source is referred to in the query: its alias if present, else its name.</summary>
    public string EffectiveName => Alias ?? RawName;

    /// <summary>The qualifier to write in generated SQL: <see cref="ReferenceText"/> when known,
    /// else the unquoted <see cref="EffectiveName"/>.</summary>
    public string EffectiveRef => ReferenceText ?? EffectiveName;
}
