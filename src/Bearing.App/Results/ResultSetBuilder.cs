using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Sql;

namespace Bearing.App.Results;

/// <summary>
/// Turns raw query results into the pageable / FK-aware / editable <see cref="ResultSetViewModel"/>s
/// the grid binds to, and summarizes a run for the status bar. Pure — no connection or UI state.
/// Extracted from the shell view-model so result construction is a named, testable unit.
/// </summary>
internal static class ResultSetBuilder
{
    /// <summary>Wrap raw query results into pageable/FK-aware/editable view models (shared by run + navigation).</summary>
    public static List<ResultSetViewModel> BuildResultSets(
        IReadOnlyList<QueryResult> results, string sql, ISchemaSnapshot? snapshot,
        Connections.ProviderTraits? traits = null)
    {
        var pageable = results.Count == 1 && results[0].Success && results[0].Columns.Count > 0;
        var statements = StatementsBehind(results, sql);
        return results
            .Select((r, i) =>
            {
                // Resolve editability (with a lock reason) only for row-returning results with a schema.
                var (target, reason) = snapshot is null || r.Columns.Count == 0
                    ? (null, null)
                    : EditabilityResolver.ResolveWithReason(snapshot, r.Columns);
                var vm = new ResultSetViewModel(r, statements?[i] ?? sql, pageable)
                {
                    // So Copy as ▸ SQL renders this engine's literals and quoting, not Postgres'.
                    Traits = traits ?? Connections.ProviderTraits.Postgres,
                    ForeignKeyColumns = DetectForeignKeyColumns(snapshot, r.Columns),
                    PrimaryKeyColumns = DetectPrimaryKeyColumns(snapshot, r.Columns),
                    EditTarget = target,
                    LockReason = target is null ? reason : null,
                };
                if (vm.IsEditable) vm.CaptureOriginals();
                return vm;
            })
            .ToList();
    }

    /// <summary>
    /// The statement behind each result set, positionally — or null when the run's own text is the honest
    /// answer for all of them (the caller then uses it for every set).
    /// <para>
    /// Two conditions have to hold, and both are checks rather than assumptions. The provider must have
    /// proven the mapping (<see cref="QueryResult.StatementIndex"/> — for Postgres, as many result sets as
    /// the driver parsed statements, so nothing was skipped), and our own split of the buffer must find the
    /// same number of statements. Then index <c>i</c> means the same statement to both, and the text is
    /// exact. If either check fails the whole batch is the answer: broad, but never the wrong statement.
    /// </para>
    /// <para>
    /// A single-set run is <b>excluded on purpose</b> even though its mapping is trivially provable. That is
    /// the one case <c>FirstPageLimiter</c> rewrites, so the statement that reached the server ends in a
    /// <c>limit 501</c> we appended — ours, not the user's, and pasting it into a report would misdescribe
    /// the result. The run's text is both correct and what the user typed there. (A batch is never
    /// rewritten: the limiter only touches a lone statement.)
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string>? StatementsBehind(IReadOnlyList<QueryResult> results, string? sql)
    {
        if (results.Count < 2 || string.IsNullOrWhiteSpace(sql)) return null;
        if (results.Any(r => r.StatementIndex is null)) return null;

        var spans = StatementSplitter.Split(sql);
        if (spans.Count != results.Count) return null;

        // Index by the provider's statement number, not by position in the list: they agree here (that is
        // what the checks above establish), and reading it off the result is what would keep this honest if
        // a provider ever reported a sparser mapping.
        return results.Select(r => spans[r.StatementIndex!.Value].Text.Trim()).ToList();
    }

    /// <summary>Result-column indices that are the primary key of their base table (for the PK badge).</summary>
    public static IReadOnlyCollection<int> DetectPrimaryKeyColumns(
        ISchemaSnapshot? snapshot, IReadOnlyList<ColumnDescriptor> columns)
    {
        if (snapshot is null || columns.Count == 0) return Array.Empty<int>();
        var pks = new List<int>();
        for (var i = 0; i < columns.Count; i++)
        {
            var c = columns[i];
            if (!c.HasBaseColumn) continue;
            if (snapshot.ColumnsOf(c.BaseTableId).Any(pc => pc.Ordinal == c.BaseColumnOrdinal && pc.IsPrimaryKey))
                pks.Add(i);
        }
        return pks;
    }

    /// <summary>Result-column indices that are foreign keys (structural, value-independent).</summary>
    public static IReadOnlyCollection<int> DetectForeignKeyColumns(
        ISchemaSnapshot? snapshot, IReadOnlyList<ColumnDescriptor> columns)
    {
        if (snapshot is null || columns.Count == 0) return Array.Empty<int>();
        var fks = new List<int>();
        for (var i = 0; i < columns.Count; i++)
            if (ForeignKeyResolver.Resolve(snapshot, columns, i) is not null) fks.Add(i);
        return fks;
    }

    /// <summary>One-line status for a run: the single set's shape, or an N-set summary. When
    /// <paramref name="wallClock"/> is supplied it is the honest end-to-end time the caller measured
    /// (connect-from-pool + execute + read), which is what the user actually waited for — preferred over
    /// the per-set server duration so the status bar never under-reports a slow run.</summary>
    public static string DescribeResults(IReadOnlyList<QueryResult> results, TimeSpan? wallClock = null)
    {
        var firstError = results.FirstOrDefault(r => !r.Success);
        if (firstError is not null)
            return $"Error{(firstError.Error?.SqlState is { } s ? $" [{s}]" : "")}: {firstError.Error?.Message}";

        // Status bar is timing-focused — the row count lives on the result's meta row, not here.
        if (results.Count == 1 && results[0].Columns.Count == 0)
            return results[0].Message ?? "Statement executed.";

        var ms = (wallClock?.TotalMilliseconds) ?? results[^1].Duration.TotalMilliseconds;
        var elapsed = FormatElapsed(ms);
        return results.Count == 1 ? $"Done · {elapsed}" : $"{results.Count} result sets · {elapsed}";
    }

    /// <summary>Human-friendly elapsed: "88 ms" under a second, "8.4 s" above — so a slow run reads honestly.
    /// Shared by the final run summary and the live status-bar execution timer so both read consistently.</summary>
    public static string FormatElapsed(double ms) =>
        ms >= 1000 ? $"{ms / 1000:0.0} s" : $"{ms:0} ms";
}
