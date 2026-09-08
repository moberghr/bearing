using System.Threading.Tasks;
using Bearing.Core.Schema;

namespace Bearing.Core.Completion;

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

    /// <summary>
    /// <see cref="Complete"/> for a caller that can await. Same answer; it differs only in not parking the
    /// calling thread while the parse runs, which matters because completion fires on every debounce tick.
    /// </summary>
    Task<CompletionResult> CompleteAsync(string sql, int caretOffset, ISchemaSnapshot schema);
}
