namespace Squirrel.Core.Completion;

public enum SuggestionKind
{
    Keyword,
    Table,
    View,
    Column,
    Alias,
    Join,
    JoinPredicate,
    Function,
    Schema,
    Snippet,
}

/// <summary>
/// A single completion candidate. Shape ported from the prototype's Suggestion (Text/Text2/Text3/
/// Replace/Priority) but <see cref="Kind"/> is an enum so the UI layer chooses the glyph, and the
/// engine stays UI-agnostic. Higher <see cref="Priority"/> sorts first.
/// </summary>
public sealed record Suggestion
{
    /// <summary>Primary label (e.g. the table or column name).</summary>
    public required string DisplayText { get; init; }

    /// <summary>Secondary detail shown dimmed (e.g. schema, or owning alias).</summary>
    public string? DetailText { get; init; }

    /// <summary>Trailing detail (e.g. a synthesized join predicate preview).</summary>
    public string? TrailingText { get; init; }

    /// <summary>Text actually inserted, replacing the caret's replacement span.</summary>
    public required string ReplacementText { get; init; }

    /// <summary>Optional long-form tooltip.</summary>
    public string? Description { get; init; }

    public SuggestionKind Kind { get; init; }

    public double Priority { get; init; }
}
