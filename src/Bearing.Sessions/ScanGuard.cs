using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bearing.Core.Schema;
using Bearing.Sql;

namespace Bearing.Sessions;

/// <summary>What <see cref="ScanGuard"/> concluded about one <c>SELECT</c>.</summary>
public enum ScanVerdictKind
{
    /// <summary>The <c>WHERE</c> pins the partition key of what it reads with <c>=</c> or <c>IN</c>: DynamoDB runs
    /// it as a Query.</summary>
    Keyed,

    /// <summary>Bearing read the statement and its target's keys, and the partition key is not pinned: DynamoDB
    /// reads the whole table (or index).</summary>
    FullScan,

    /// <summary>Bearing could not check. Never a claim that the statement scans — only that nobody showed it
    /// does not (see <see cref="ScanUncertainty"/> for which fact was missing).</summary>
    Undetermined,
}

/// <summary>Which fact an <see cref="ScanVerdictKind.Undetermined"/> verdict was missing. Kept typed, because
/// "the statement could not be read", "no such table" and "the keys were not loaded" are different answers
/// (§1.1).</summary>
public enum ScanUncertainty
{
    /// <summary>The lexical reduction could not follow the statement (<see cref="ScanTarget.Readable"/> false).</summary>
    Unreadable,

    /// <summary>The caller had no table list to look the target up in.</summary>
    TablesNotLoaded,

    /// <summary>The table list Bearing loaded has no table by that exact name.</summary>
    UnknownTable,

    /// <summary>The table's loaded indexes have no secondary index by that exact name.</summary>
    UnknownIndex,

    /// <summary>The table (or index) is known, but its partition key was not loaded.</summary>
    KeysNotLoaded,
}

/// <summary>
/// One <c>SELECT</c>'s verdict, with what the sentences name.
/// </summary>
/// <param name="Target">The statement as <see cref="ISqlDialect.ScanTargets"/> read it.</param>
/// <param name="Kind">The verdict.</param>
/// <param name="PartitionKey">The partition key of the table or index read; null when it was not found.</param>
/// <param name="ApproximateItemCount">DynamoDB's own estimate of the table's item count, when the caller had
/// one.</param>
/// <param name="Uncertainty">Which fact was missing; set only on <see cref="ScanVerdictKind.Undetermined"/>.</param>
public sealed record ScanVerdict(
    ScanTarget Target,
    ScanVerdictKind Kind,
    string? PartitionKey,
    long? ApproximateItemCount,
    ScanUncertainty? Uncertainty)
{
    /// <summary>
    /// A <see cref="ScanVerdictKind.FullScan"/> of a readable target with no <c>WHERE</c> at all
    /// (<see cref="ScanTarget.HasWhere"/> false): every item it evaluates is returned, so its first page reads about
    /// one page of items. The App runs that first page without asking (<see cref="ScanGuard.AsksBeforeRun"/>); its
    /// result still asks before Fetch all and Export. Never set on any other verdict — a <c>WHERE</c> that does not
    /// pin the key can evaluate many requests' worth of items to fill one page — and it changes nothing for the
    /// <c>bearing</c> command, which runs keyed reads only.
    /// </summary>
    public bool Unfiltered { get; init; }
}

/// <summary>
/// Whether a DynamoDB <c>SELECT</c> reads a whole table, and the sentences that say so (<i>Behaviour 8</i> and
/// <i>12</i>). Combines the lexical half (<see cref="ISqlDialect.ScanTargets"/>: the table, the index, the
/// attributes the <c>WHERE</c> pins) with the half only a session holds — the target's partition key.
/// <para>
/// <b>Pure.</b> The caller loads what it can within <see cref="Budget"/> — the snapshot for tables, the
/// <see cref="TableDetails"/> of each table read through an index, the size entry for an item count — and hands
/// over whatever arrived. Anything that did not arrive makes the verdict
/// <see cref="ScanVerdictKind.Undetermined"/>, never <see cref="ScanVerdictKind.FullScan"/>: Bearing says it
/// could not check, not that the statement scans (§1.1).
/// </para>
/// <para>
/// Here rather than in a host because it decides whether a statement may run (§2.6): the App asks before a scan
/// (<see cref="AsksBeforeRun"/> — an unfiltered one's first page excepted), the <c>bearing</c> command refuses
/// anything not <see cref="ScanVerdictKind.Keyed"/>, and both say it in these words. Accident prevention, not a boundary — see <see cref="Boundary"/>.
/// </para>
/// </summary>
public static class ScanGuard
{
    /// <summary>How long a caller may spend connecting and loading the table list and keys before it classifies
    /// with what it has.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    /// <summary>What the check is and is not, for the confirmation and the connection setting's note.</summary>
    public const string Boundary =
        "This check stops mistakes; it is not cost control and not a guarantee. The boundary is an IAM policy "
        + "using the dynamodb:FullTableScan condition key.";

    /// <summary>How a scan's rows arrive, for the confirmation.</summary>
    public const string Paging =
        "Rows load a page at a time; Load more and Fetch all keep reading the same scan.";

    /// <summary>
    /// The statements of <paramref name="sql"/> to classify, or null when <paramref name="dialect"/> has no scan
    /// question (every engine but DynamoDB). <see cref="ISqlDialect.ScanTargets"/>, <b>padded fail-closed</b>:
    /// when the write guard's reading of the batch holds more reads (statements led by one of
    /// <see cref="ISqlDialect.ExternalReadVerbs"/>, or ones it could not read at all) than the reduction returned
    /// targets for, a read went unfollowed, and one unreadable target is added for it — so the batch is
    /// <see cref="ScanVerdictKind.Undetermined"/>, asked about or refused, never run unclassified. Not padded when
    /// a target is already unreadable: that one already stops the batch.
    /// </summary>
    public static IReadOnlyList<ScanTarget>? TargetsFor(ISqlDialect dialect, string sql)
    {
        if (dialect.ScanTargets(sql) is not { } targets) return null;

        var reads = WriteGuard.Describe(dialect, sql)
            .Count(s => !s.GuardIsDialectAware || dialect.ExternalReadVerbs.Contains(s.Verb));

        return targets.Count < reads && targets.All(t => t.Readable)
            ? [.. targets, Unfollowed]
            : targets;
    }

    /// <summary>The target standing in for a read the reduction did not follow (<see cref="TargetsFor"/>).</summary>
    private static readonly ScanTarget Unfollowed =
        new(Statement: "", Table: null, Index: null, PinnedAttributes: [], Readable: false);

    /// <summary>
    /// The table <paramref name="target"/> reads, matched by its exact name — DynamoDB table names are
    /// case-sensitive, so <c>orders</c> is not <c>Orders</c>. Null when the target names no table, or the
    /// snapshot has none by that name. For a caller deciding which tables' details to load.
    /// </summary>
    public static TableInfo? TableOf(ScanTarget target, ISchemaSnapshot? snapshot)
    {
        if (target.Table is not { } name || snapshot is null) return null;

        return snapshot.Tables
            .Where(t => string.Equals(t.Name, name, StringComparison.Ordinal))
            .FirstOrDefault();
    }

    /// <summary>
    /// Classify one target.
    /// </summary>
    /// <param name="target">The statement's lexical reduction.</param>
    /// <param name="snapshot">The connection's table list, or null when it was not loaded.</param>
    /// <param name="details">A table's details, or null when they were not loaded. Asked only for a target that
    /// names an index.</param>
    /// <param name="itemCounts">DynamoDB's approximate item count for a table, or null when unknown.</param>
    public static ScanVerdict Classify(
        ScanTarget target,
        ISchemaSnapshot? snapshot,
        Func<TableInfo, TableDetails?>? details = null,
        Func<TableInfo, long?>? itemCounts = null)
    {
        if (!target.Readable) return Undetermined(target, ScanUncertainty.Unreadable);
        if (snapshot is null) return Undetermined(target, ScanUncertainty.TablesNotLoaded);
        if (TableOf(target, snapshot) is not { } table) return Undetermined(target, ScanUncertainty.UnknownTable);

        // Zero is not reported as a fact: DynamoDB's count lags by hours, and "about 0 items" beside a table that
        // has filled since would be a claim nobody checked (SF7). Only a positive figure is named.
        var reported = itemCounts?.Invoke(table);
        var count = reported > 0 ? reported : null;
        var columns = snapshot.ColumnsOf(table.Id);
        string? partitionKey;

        if (target.Index is { } indexName)
        {
            var loaded = details?.Invoke(table);
            if (loaded is null || loaded.Indexes.Count == 0)
            {
                return Undetermined(target, ScanUncertainty.KeysNotLoaded, count);
            }

            // The primary key is listed among the indexes too (named PRIMARY), and a secondary index may share
            // that name — only a secondary index is something "Table"."Index" can address.
            var index = loaded.Indexes
                .Where(i => !i.IsPrimary)
                .Where(i => string.Equals(i.Name, indexName, StringComparison.Ordinal))
                .FirstOrDefault();
            if (index is null) return Undetermined(target, ScanUncertainty.UnknownIndex, count);

            partitionKey = index.Ordinals.Count == 0 ? null : NameAt(columns, index.Ordinals[0]);
        }
        else
        {
            partitionKey = columns
                .Where(c => c.Ordinal == 1)
                .Where(c => c.IsPrimaryKey)
                .Select(c => c.Name)
                .FirstOrDefault();
        }

        if (partitionKey is null) return Undetermined(target, ScanUncertainty.KeysNotLoaded, count);

        // Attribute names are case-sensitive: a pin on "PK" does not pin "pk".
        var kind = target.PinnedAttributes.Contains(partitionKey, StringComparer.Ordinal)
            ? ScanVerdictKind.Keyed
            : ScanVerdictKind.FullScan;

        return new ScanVerdict(target, kind, partitionKey, count, Uncertainty: null)
        {
            Unfiltered = kind == ScanVerdictKind.FullScan && !target.HasWhere,
        };
    }

    /// <summary>
    /// Whether the App asks before running <paramref name="verdict"/>'s statement (with "Confirm full table scans"
    /// on): every verdict that is not <see cref="ScanVerdictKind.Keyed"/>, except an
    /// <see cref="ScanVerdict.Unfiltered"/> scan, whose first page runs unasked. Not the <c>bearing</c> command's
    /// rule — it refuses anything not keyed (<see cref="CliRefusal"/>).
    /// </summary>
    public static bool AsksBeforeRun(ScanVerdict verdict)
        => verdict.Kind != ScanVerdictKind.Keyed && !verdict.Unfiltered;

    /// <summary>Classify each target, in order.</summary>
    public static IReadOnlyList<ScanVerdict> Classify(
        IEnumerable<ScanTarget> targets,
        ISchemaSnapshot? snapshot,
        Func<TableInfo, TableDetails?>? details = null,
        Func<TableInfo, long?>? itemCounts = null)
        => targets.Select(t => Classify(t, snapshot, details, itemCounts)).ToList();

    /// <summary>
    /// The one-sentence description of a verdict that is not <see cref="ScanVerdictKind.Keyed"/>: for a scan,
    /// what it reads and which key the <c>WHERE</c> lacks (with the item count when known); for an undetermined
    /// one, that Bearing could not check and why. Null for a keyed verdict.
    /// </summary>
    public static string? Describe(ScanVerdict verdict)
    {
        switch (verdict.Kind)
        {
            case ScanVerdictKind.FullScan:
                {
                    var target = verdict.Target;
                    var reads = target.Index is { } index
                        ? $"index {Quote(index)} of {Quote(target.Table!)}"
                        : Quote(target.Table!);
                    var key = target.Index is null ? "its partition key" : "the index's partition key";
                    var sentence = $"This SELECT would read the whole of {reads} — the WHERE has no = or IN on "
                                   + $"{key} {Quote(verdict.PartitionKey!)}.";

                    // "Last reported", not "holds": DynamoDB refreshes the figure about every six hours, so it
                    // is a past reading, not a count of the table now (SF7).
                    return verdict.ApproximateItemCount is { } count
                        ? $"{sentence} DynamoDB last reported about {Items(count)} in {Quote(target.Table!)}."
                        : sentence;
                }

            case ScanVerdictKind.Undetermined:
                return $"Bearing could not check whether this SELECT{Subject(verdict.Target)} is keyed: "
                       + $"{Why(verdict)}.";

            default:
                return null;
        }
    }

    /// <summary>
    /// The confirmation's text for a batch: one sentence per statement that is not keyed, then how rows load and
    /// what the check is (<see cref="Paging"/>, <see cref="Boundary"/>). Null when every verdict is keyed —
    /// nothing to ask. A caller that skips unfiltered scans passes only the verdicts it asks about
    /// (<see cref="AsksBeforeRun"/>).
    /// </summary>
    public static string? ConfirmationText(IEnumerable<ScanVerdict> verdicts)
    {
        var sentences = verdicts
            .Select(Describe)
            .OfType<string>()
            .ToList();
        if (sentences.Count == 0) return null;

        sentences.Add(Paging);
        sentences.Add(Boundary);

        return string.Join("\n\n", sentences);
    }

    /// <summary>
    /// The <c>bearing</c> command's refusal for a statement that is not keyed (<i>Behaviour 12</i>), or null for
    /// a keyed one. The command runs keyed reads only, whatever the connection's "Confirm full table scans"
    /// setting says: nobody is there to confirm a scan.
    /// </summary>
    public static string? CliRefusal(string connectionName, ScanVerdict verdict)
    {
        var target = verdict.Target;

        return verdict.Kind switch
        {
            ScanVerdictKind.FullScan =>
                $"'{connectionName}': this SELECT would read the whole of {QualifiedName(target)} — the WHERE has "
                + $"no = or IN on its partition key {Quote(verdict.PartitionKey!)}. The bearing command runs keyed "
                + "reads only; add a partition-key condition, or run it in Bearing.",
            ScanVerdictKind.Undetermined =>
                $"'{connectionName}': Bearing could not check whether this SELECT{Subject(target)} is keyed: "
                + $"{Why(verdict)}. The bearing command runs keyed reads only, so it was not run; run it in "
                + "Bearing.",
            _ => null,
        };
    }

    private static ScanVerdict Undetermined(ScanTarget target, ScanUncertainty why, long? count = null)
        => new(target, ScanVerdictKind.Undetermined, PartitionKey: null, count, why);

    private static string? NameAt(IReadOnlyList<ColumnInfo> columns, int ordinal)
        => columns
            .Where(c => c.Ordinal == ordinal)
            .Select(c => c.Name)
            .FirstOrDefault();

    /// <summary>" on "T"" / " on index "I" of "T"" / "" — what the statement reads, when it was read.</summary>
    private static string Subject(ScanTarget target)
    {
        if (target.Table is not { } table) return "";

        return target.Index is { } index
            ? $" on index {Quote(index)} of {Quote(table)}"
            : $" on {Quote(table)}";
    }

    private static string Why(ScanVerdict verdict)
    {
        var target = verdict.Target;
        var table = target.Table is { } t ? Quote(t) : "the table";

        return verdict.Uncertainty switch
        {
            ScanUncertainty.TablesNotLoaded => "the table list for this connection was not loaded",
            ScanUncertainty.UnknownTable => $"the table list Bearing loaded has no table named {table}",
            ScanUncertainty.UnknownIndex =>
                $"the indexes Bearing loaded for {table} include none named {Quote(target.Index!)}",
            ScanUncertainty.KeysNotLoaded => target.Index is null
                ? $"the partition key of {table} was not loaded"
                : $"the partition key of index {Quote(target.Index)} was not loaded",
            _ => "its FROM and WHERE could not be followed",
        };
    }

    /// <summary><c>"T"</c>, or <c>"T"."I"</c> for an index — as the statement addresses it.</summary>
    private static string QualifiedName(ScanTarget target)
        => target.Index is { } index ? $"{Quote(target.Table!)}.{Quote(index)}" : Quote(target.Table!);

    private static string Quote(string name) => $"\"{name}\"";

    private static string Items(long count)
        => count == 1 ? "1 item" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} items";
}
