using Squirrel.Core.Schema;

namespace Squirrel.Core.Completion;

/// <summary>
/// Output of a completion request: the ranked suggestions plus the span of source text they replace
/// on commit (so a partially-typed identifier under the caret is overwritten, not appended to).
/// </summary>
public sealed record CompletionResult(
    IReadOnlyList<Suggestion> Suggestions,
    int ReplacementStart,
    int ReplacementLength)
{
    public static readonly CompletionResult Empty = new(Array.Empty<Suggestion>(), 0, 0);
}

/// <summary>The completion engine: pure, synchronous, no I/O. Runs off the UI thread.</summary>
public interface ICompletionEngine
{
    CompletionResult Complete(string sql, int caretOffset, ISchemaSnapshot schema);
}
