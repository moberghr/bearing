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

/// <summary>
/// The completion engine: pure, no I/O.
/// <para>
/// <b>Call it from the UI thread.</b> An implementation may resolve UI-thread-affine state — which grammar
/// the selected tab is written in — before handing the work to a thread of its own, so entering it from a
/// pool thread is what an implementation cannot defend against. This summary used to say the opposite
/// ("runs off the UI thread"), and following it is what killed completion for the whole of 1.0: the answer
/// was read off the window's <c>DataContext</c> on a parse thread and threw. The parse itself does not run
/// here — <c>CompleteAsync</c> returns before it starts — so the caller is not blocked by obeying this.
/// </para>
/// </summary>
public interface ICompletionEngine
{
    CompletionResult Complete(string sql, int caretOffset, ISchemaSnapshot schema);

    /// <summary>
    /// <see cref="Complete"/> for a caller that can await. Same answer; it differs only in not parking the
    /// calling thread while the parse runs, which matters because completion fires on every debounce tick.
    /// </summary>
    Task<CompletionResult> CompleteAsync(string sql, int caretOffset, ISchemaSnapshot schema);
}
