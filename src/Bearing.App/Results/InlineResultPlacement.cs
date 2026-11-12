using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.ViewModels;
using Bearing.Sql;

namespace Bearing.App.Results;

/// <summary>
/// What a run covered in the editor buffer: the SQL it sends, and where in the buffer each statement it sends
/// ends — so a result can be placed under the statement that produced it.
/// </summary>
/// <param name="Sql">The text handed to execution (normalized by <see cref="StatementSplitter.EnsureSeparated"/>).</param>
/// <param name="StatementEnds">Buffer offset just past each statement actually sent, in sending order —
/// position <c>i</c> is what a provider's <c>StatementIndex == i</c> refers to.</param>
/// <param name="Start">Buffer offset of the first non-whitespace character the run covered.</param>
/// <param name="End">Buffer offset just past the last non-whitespace character the run covered.</param>
/// <param name="IsWholeBuffer">Run All: every statement in the buffer, so its results replace every inline
/// result in the tab and may be split per statement.</param>
public sealed record RunScope(string Sql, IReadOnlyList<int> StatementEnds, int Start, int End, bool IsWholeBuffer)
{
    /// <summary>Whether a run over this scope replaces the zone hanging at <paramref name="offset"/>: Run All
    /// replaces every zone, a Run the zones inside the text it ran. Exclusive at Start: a zone sitting exactly
    /// there belongs to the statement before, whose end touches this one's start when nothing separates them
    /// (<c>select 1;select 2;</c>). The one rule — the run and the unsaved-edits guard both ask it.</summary>
    public bool Replaces(int offset) => IsWholeBuffer || (offset > Start && offset <= End);

    /// <summary>The buffer above the run — where the <c>@set</c> lines that give its <c>:name</c> variables
    /// their values may sit (#103). Empty for Run All, whose text is the whole buffer.</summary>
    public string Preamble { get; init; } = "";
}

/// <summary>
/// Decides where inline results go (spec: docs/specs/2026-11-06-inline-results.md). Pure.
/// <para>
/// Run puts everything it returned under the text it ran. Run All gives each statement its own result only
/// when the provider <b>proved</b> which statement produced which set (<see cref="ResultSetViewModel.StatementIndex"/>
/// on every set); otherwise the whole batch lands in one place under the last statement, captioned as a batch.
/// A per-statement split is never inferred from positions (§1.1).
/// </para>
/// </summary>
internal static class InlineResultPlacement
{
    /// <summary>The scope of running <paramref name="text"/>, which starts at buffer offset
    /// <paramref name="start"/> (0 for the whole buffer, the selection start for a selection).</summary>
    public static RunScope Scope(ISqlDialect dialect, string text, int start, bool isWholeBuffer)
    {
        // One split, shared with EnsureSeparated, and one lex per statement for where its code ends: this runs
        // on the UI thread before every Run, and a long script read four times over shows as input lag.
        var spans = StatementSplitter.Split(dialect, text);
        var (ends, codeEnd) = Ends(dialect, spans, start);
        var leading = text.Length - text.TrimStart().Length;
        // Where the code ends, not the text: a statement's span may carry the comment lines after it, and its
        // results belong under the statement, not under a comment heading the next section.
        var trimmedEnd = codeEnd ?? start + text.TrimEnd().Length;
        return new RunScope(StatementSplitter.EnsureSeparated(dialect, text, spans), ends,
            Math.Min(start + leading, trimmedEnd), trimmedEnd, isWholeBuffer);
    }

    /// <summary>Where each sent statement of <paramref name="text"/> ends — <see cref="RunScope.StatementEnds"/>
    /// of the whole buffer, without building the SQL a run would send.</summary>
    public static IReadOnlyList<int> StatementEnds(ISqlDialect dialect, string text)
        => Ends(dialect, StatementSplitter.Split(dialect, text), 0).Ends;

    private static (List<int> Ends, int? CodeEnd) Ends(ISqlDialect dialect, IReadOnlyList<StatementSpan> spans, int start)
    {
        var ends = new List<int>();
        int? codeEnd = null;
        foreach (var span in spans)
        {
            if (StatementSplitter.SentText(span.Text).Length == 0)
            {
                continue;
            }
            var code = dialect.CodeEnd(span.Text);
            ends.Add(start + (code is { } c ? span.Start + c : span.TrimmedEnd));
            if (code is not null)
            {
                codeEnd = ends[^1];
            }
        }
        return (ends, codeEnd);
    }

    /// <summary>The inline results for <paramref name="sets"/> returned by a run over <paramref name="scope"/>.</summary>
    public static IReadOnlyList<InlineResultZone> Assign(RunScope scope, IReadOnlyList<ResultSetViewModel> sets)
    {
        if (sets.Count == 0)
        {
            return Array.Empty<InlineResultZone>();
        }

        var ends = scope.StatementEnds;
        // Both checks ResultSetBuilder.StatementsBehind makes before trusting the same index: the provider
        // proved it (a number on every set — Postgres numbers only when it got a set per statement it parsed),
        // and our split found as many statements as there are sets. Without the second, a lexer disagreement
        // over one part would shift every zone onto its neighbour's statement.
        var proven = scope.IsWholeBuffer
            && ends.Count > 0
            && sets.Count == ends.Count
            && sets.All(s => s.StatementIndex is { } i && i >= 0 && i < ends.Count);
        if (proven)
        {
            return sets
                .GroupBy(s => s.StatementIndex!.Value)
                .OrderBy(g => g.Key)
                .Select(g => new InlineResultZone(ends[g.Key], g.ToList(), caption: null))
                .ToList();
        }

        var offset = scope.IsWholeBuffer && ends.Count > 0 ? ends[^1] : scope.End;
        string? caption = sets.Count < 2
            ? null
            : scope.IsWholeBuffer && ends.Count > 1
                ? $"{sets.Count} result sets from this batch"
                : $"{sets.Count} result sets";
        return [new InlineResultZone(offset, sets, caption, fromWholeBuffer: scope.IsWholeBuffer && ends.Count > 1,
            statementCount: Math.Max(1, ends.Count))];
    }

    /// <summary>
    /// The buffer span a zone's statements occupy now, to re-run them from the zone (spec §8): the statement
    /// the zone hangs under — at <paramref name="offset"/>, its last code character — and the
    /// <paramref name="statementCount"/> − 1 sent statements before it, as many as the run that made the zone
    /// covered (a multi-statement selection). Null when no statement sits there any more.
    /// </summary>
    public static (int Start, int End)? ZoneSpan(ISqlDialect dialect, string text, int offset, int statementCount)
    {
        var at = Math.Clamp(offset, 0, text.Length);
        var sent = StatementSplitter.Split(dialect, text)
            .Where(s => StatementSplitter.SentText(s.Text).Length > 0)
            .ToList();
        // The character before the offset is inside the statement for certain; the offset itself can be the
        // boundary with the next one.
        var last = sent.FindIndex(s => at > s.Start && at - 1 < s.End);
        if (last < 0)
        {
            last = sent.FindIndex(s => at >= s.Start && at <= s.End);
        }
        if (last < 0)
        {
            return null;
        }
        var first = Math.Max(0, last - Math.Max(1, statementCount) + 1);
        return (sent[first].Start, sent[last].End);
    }
}
