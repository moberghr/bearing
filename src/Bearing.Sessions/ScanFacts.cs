using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Sql;

namespace Bearing.Sessions;

/// <summary>
/// The half of the full-scan check only a session holds: gathering what <see cref="ScanGuard.Classify(IEnumerable{ScanTarget}, ISchemaSnapshot?, Func{TableInfo, TableDetails?}?, Func{TableInfo, long?}?)"/>
/// needs, within a budget, and classifying with whatever arrived. One copy for both hosts (§2.6, ARCH1) — the App
/// asks before a scan, the <c>bearing</c> command refuses one; what they look up, how long they wait and what a
/// missing fact means are the same, and only their sentences and dialogs differ.
/// <para>
/// <b>What is looked up.</b> The table list (the session's snapshot, or the last one cached when none arrives in
/// time), the <see cref="TableDetails"/> of each table read through an index, and — when the caller wants them,
/// and only beside a statement that would scan — the item count of each scanned table, one table at a time
/// (<see cref="IMetadataReader.GetRelationSizeAsync"/>), never a whole region's sizes.
/// </para>
/// <para>
/// <b>Waited for, never cancelled.</b> The connect and the schema load are shared: the run that follows joins the
/// same in-flight connect and the same load, so the budget stops <em>waiting</em> for them
/// (<see cref="Task.WaitAsync(TimeSpan)"/>) rather than cancelling them, which would make the run start both again.
/// Whatever has not arrived when the budget runs out makes a verdict <see cref="ScanVerdictKind.Undetermined"/> —
/// never a reason to run unasked, and never a claim that the statement scans (§1.1).
/// </para>
/// </summary>
public static class ScanFacts
{
    /// <summary>
    /// Classify <paramref name="targets"/> (from <see cref="ScanGuard.TargetsFor"/>, already padded) for
    /// <paramref name="info"/>.
    /// </summary>
    /// <param name="sessions">Where the session, the table list and the keys come from.</param>
    /// <param name="info">The connection the statements would run on.</param>
    /// <param name="targets">The statements to classify.</param>
    /// <param name="budget">How long to wait for the connect and the reads before classifying with what arrived.</param>
    /// <param name="withItemCounts">Read the item count of each table a statement would scan, for the sentence
    /// that names it. The <c>bearing</c> command's refusal names none.</param>
    /// <param name="connected">A session the caller already connected, or null to connect here within the budget.
    /// The <c>bearing</c> command connects first so a sign-in failure reads as one, rather than as "could not
    /// check".</param>
    /// <param name="ct">The caller's own cancellation, which is thrown — only the budget is swallowed.</param>
    public static async Task<IReadOnlyList<ScanVerdict>> ClassifyAsync(
        IConnectionSessionManager sessions,
        ConnectionInfo info,
        IReadOnlyList<ScanTarget> targets,
        TimeSpan budget,
        bool withItemCounts,
        ConnectionSession? connected,
        CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget);
        var token = deadline.Token;

        var details = new Dictionary<long, TableDetails>();
        var counts = new Dictionary<long, long?>();
        // The cached table list is the fallback when no session can be had in time: a DynamoDB key schema
        // cannot change after the table is created, so last time's answer about a table's partition key holds.
        var snapshot = sessions.TryGetSnapshot(info.Id, info.Database);

        try
        {
            var session = connected
                          ?? await sessions.GetOrConnectAsync(info, CancellationToken.None).WaitAsync(token)
                              .ConfigureAwait(false);
            using var lease = connected is null ? sessions.Lease(session) : null;

            snapshot = await sessions.EnsureSchemaAsync(session, CancellationToken.None).WaitAsync(token)
                           .ConfigureAwait(false)
                       ?? snapshot;

            foreach (var table in targets
                         .Where(t => t.Index is not null)
                         .Select(t => ScanGuard.TableOf(t, snapshot))
                         .OfType<TableInfo>()
                         .DistinctBy(t => t.Id))
            {
                details[table.Id] = await session.Metadata.GetTableDetailsAsync(table.Id, token).ConfigureAwait(false);
            }

            // Item counts only matter beside a scan, so they are read only for the tables that would be scanned.
            if (withItemCounts)
            {
                foreach (var table in ScanGuard.Classify(targets, snapshot, Details)
                             .Where(v => v.Kind == ScanVerdictKind.FullScan)
                             .Select(v => ScanGuard.TableOf(v.Target, snapshot))
                             .OfType<TableInfo>()
                             .DistinctBy(t => t.Id))
                {
                    var size = await session.Metadata.GetRelationSizeAsync(table.Id, token).ConfigureAwait(false);
                    counts[table.Id] = size?.EstimatedRows;
                }
            }
        }
        // A connect that failed, a read that failed, or the budget running out all leave the same answer:
        // classify with what arrived. The caller's own cancellation is not swallowed.
        catch (Exception) when (!ct.IsCancellationRequested)
        {
        }

        ct.ThrowIfCancellationRequested();

        return ScanGuard.Classify(targets, snapshot, Details, Counts);

        TableDetails? Details(TableInfo table) => details.GetValueOrDefault(table.Id);

        long? Counts(TableInfo table) => counts.GetValueOrDefault(table.Id);
    }
}
