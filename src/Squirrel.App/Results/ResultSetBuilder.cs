using System;
using System.Collections.Generic;
using System.Linq;
using Squirrel.App.ViewModels;
using Squirrel.Core.Data;
using Squirrel.Core.Schema;

namespace Squirrel.App.Results;

/// <summary>
/// Turns raw query results into the pageable / FK-aware / editable <see cref="ResultSetViewModel"/>s
/// the grid binds to, and summarizes a run for the status bar. Pure — no connection or UI state.
/// Extracted from the shell view-model so result construction is a named, testable unit.
/// </summary>
internal static class ResultSetBuilder
{
    /// <summary>Wrap raw query results into pageable/FK-aware/editable view models (shared by run + navigation).</summary>
    public static List<ResultSetViewModel> BuildResultSets(
        IReadOnlyList<QueryResult> results, string sql, ISchemaSnapshot? snapshot)
    {
        var pageable = results.Count == 1 && results[0].Success && results[0].Columns.Count > 0;
        return results
            .Select(r =>
            {
                // Resolve editability (with a lock reason) only for row-returning results with a schema.
                var (target, reason) = snapshot is null || r.Columns.Count == 0
                    ? (null, null)
                    : EditabilityResolver.ResolveWithReason(snapshot, r.Columns);
                var vm = new ResultSetViewModel(r, sql, pageable)
                {
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
            if (snapshot.ColumnsOf(c.BaseTableOid).Any(pc => pc.AttNum == c.BaseColumnAttNum && pc.IsPrimaryKey))
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

    /// <summary>One-line status for a run: the single set's shape, or an N-set summary.</summary>
    public static string DescribeResults(IReadOnlyList<QueryResult> results)
    {
        var firstError = results.FirstOrDefault(r => !r.Success);
        if (firstError is not null)
            return $"Error{(firstError.Error?.SqlState is { } s ? $" [{s}]" : "")}: {firstError.Error?.Message}";

        // Status bar is timing-focused — the row count lives on the result's meta row, not here.
        if (results.Count == 1)
        {
            var r = results[0];
            if (r.Columns.Count == 0) return r.Message ?? "Statement executed.";
            return $"Done · {r.Duration.TotalMilliseconds:0} ms";
        }

        var elapsed = results[^1].Duration.TotalMilliseconds;
        return $"{results.Count} result sets · {elapsed:0} ms";
    }
}
