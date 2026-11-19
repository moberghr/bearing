using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Sessions;
using Bearing.Sql;

namespace Bearing.App.Services;

/// <summary>Which question a scan confirmation asks — it decides how the prompt reads and what its button says.</summary>
public enum ScanQuestionKind
{
    /// <summary>Before a run whose statements may read a whole table.</summary>
    Run,

    /// <summary>Before Fetch all (or an Export, which fetches all first) on a result whose run was confirmed as a
    /// possible scan.</summary>
    FetchAll,
}

/// <summary>
/// One full-scan question as the dialog shows it (<i>Behaviour 8</i>). Pure: every sentence is derived here, from
/// <see cref="ScanGuard"/>'s, so it can be tested without a window (§2.5, §4.3).
/// </summary>
/// <param name="Connection">Where the read lands.</param>
/// <param name="Kind">Which question.</param>
/// <param name="Verdicts">The statements that are not keyed, in batch order; empty for <see cref="ScanQuestionKind.FetchAll"/>.</param>
/// <param name="Text">The body: one sentence per statement, how rows load and what the check is.</param>
public sealed record ScanQuestion(
    ConnectionInfo Connection,
    ScanQuestionKind Kind,
    IReadOnlyList<ScanVerdict> Verdicts,
    string Text)
{
    public string Title => Kind == ScanQuestionKind.Run ? "Confirm full table scan" : "Confirm fetch all";

    /// <summary>Connection name plus its environment label, as the write confirmation names it.</summary>
    public string Target => string.IsNullOrWhiteSpace(Connection.Environment)
        ? Connection.Name
        : $"{Connection.Name} · {Connection.Environment}";

    /// <summary>
    /// The question. Says "may read a whole table" when every statement it asks about is one Bearing could not
    /// check — never "this is a scan" for those (§1.1).
    /// </summary>
    public string Heading => Kind switch
    {
        ScanQuestionKind.FetchAll => $"Keep reading the whole table on {Target}?",
        _ when Verdicts.Any(v => v.Kind == ScanVerdictKind.FullScan) => $"Read a whole table on {Target}?",
        _ => $"Run on {Target}? Bearing could not check whether it reads a whole table.",
    };

    public string ConfirmLabel => Kind == ScanQuestionKind.Run ? "Run anyway" : "Fetch all";
}

/// <summary>What the scan check decided for one run.</summary>
/// <param name="Proceed">False when the user cancelled (or nobody could be asked): nothing runs.</param>
/// <param name="IsFullScan">The run was confirmed as a possible scan, so its result sets ask once more before
/// Fetch all or Export (<see cref="ResultSetViewModel.IsFullScan"/>).</param>
public readonly record struct ScanDecision(bool Proceed, bool IsFullScan)
{
    /// <summary>Nothing to ask: the setting is off, the engine has no scan question, or every read is keyed.</summary>
    public static ScanDecision NotAsked => new(Proceed: true, IsFullScan: false);

    public static ScanDecision Confirmed => new(Proceed: true, IsFullScan: true);

    public static ScanDecision Cancelled => new(Proceed: false, IsFullScan: false);
}

/// <summary>
/// The App half of DynamoDB's full-scan question (<i>Behaviour 8</i>, SC14): before a run, and again before Fetch
/// all / Export on a result confirmed as a scan.
/// <para>
/// <b>Who is asked.</b> Only a connection with <see cref="ConnectionInfo.ConfirmFullScans"/> on, on an engine whose
/// dialect has a scan question (<see cref="ISqlDialect.ScanTargets"/> non-null — DynamoDB only). Everything else
/// returns before any lookup, so no other engine ever connects, loads or asks here.
/// </para>
/// <para>
/// <b>What is looked up.</b> The table list (the snapshot), the <see cref="TableDetails"/> of each table read
/// through an index, and the item counts for a table that would be scanned. Looking them up may connect — the run
/// would anyway — but the whole lookup is bounded by <see cref="Budget"/>; whatever did not arrive makes the
/// verdict <see cref="ScanVerdictKind.Undetermined"/>, which asks and says Bearing could not check (never that it
/// scans). The lookup holds no lease while the dialog is open.
/// </para>
/// <para>
/// Accident prevention, not a boundary: the sentences come from <see cref="ScanGuard"/>, which names an IAM policy
/// as the boundary. Lifted out of <see cref="ExecutionViewModel"/> (§9.1), which makes one delegating call per
/// entry point.
/// </para>
/// </summary>
public sealed class ScanConfirmation
{
    private readonly IConnectionSessionManager _sessions;
    private readonly IDialogService? _dialogs;
    private readonly Action<string> _status;

    /// <param name="sessions">Where the table list and keys are read from.</param>
    /// <param name="dialogs">Who is asked. Null (no window) cancels any run that would have been asked about:
    /// a scan nobody confirmed does not run.</param>
    /// <param name="status">The status line, for the check's progress and a cancellation.</param>
    public ScanConfirmation(IConnectionSessionManager sessions, IDialogService? dialogs, Action<string> status)
    {
        _sessions = sessions;
        _dialogs = dialogs;
        _status = status;
    }

    /// <summary>How long the lookup may take before classifying with what arrived. <see cref="ScanGuard.Budget"/>;
    /// settable so a test need not wait four seconds.</summary>
    internal TimeSpan Budget { get; init; } = ScanGuard.Budget;

    /// <summary>
    /// The statements to check, or null when nothing is ever asked: the setting is off, or the engine has no scan
    /// question (every engine but DynamoDB), or the batch has no <c>SELECT</c> that reads a table.
    /// </summary>
    public static IReadOnlyList<ScanTarget>? TargetsToCheck(ConnectionInfo info, ISqlDialect dialect, string sql)
    {
        if (!info.ConfirmFullScans) return null;

        return dialect.ScanTargets(sql) is { Count: > 0 } targets ? targets : null;
    }

    /// <summary>The run's question, or null when every verdict is keyed — nothing to ask.</summary>
    public static ScanQuestion? QuestionFor(ConnectionInfo info, IReadOnlyList<ScanVerdict> verdicts)
    {
        if (ScanGuard.ConfirmationText(verdicts) is not { } text) return null;

        return new ScanQuestion(
            info,
            ScanQuestionKind.Run,
            verdicts.Where(v => v.Kind != ScanVerdictKind.Keyed).ToList(),
            text);
    }

    /// <summary>
    /// Fetch all's second question, naming the Fetch-all row limit (<paramref name="fetchAllLimit"/>). Load more
    /// never asks again — it reads one page of the scan the user already confirmed.
    /// </summary>
    public static ScanQuestion FetchAllQuestion(ConnectionInfo info, int fetchAllLimit)
    {
        var limit = Math.Max(1, fetchAllLimit).ToString("N0", CultureInfo.InvariantCulture);
        var text =
            "This result comes from a SELECT that was confirmed before it ran because it may read a whole table. "
            + $"Fetch all keeps reading it until it ends or reaches the Fetch all limit of {limit} rows "
            + "(Settings ▸ Results). Export fetches all first, the same way."
            + "\n\n" + ScanGuard.Boundary;

        return new ScanQuestion(info, ScanQuestionKind.FetchAll, [], text);
    }

    /// <summary>Mark a run's result sets as coming from a confirmed scan, so Fetch all / Export ask again.</summary>
    public static void Mark(IEnumerable<ResultSetViewModel> sets, bool isFullScan)
    {
        if (!isFullScan) return;

        foreach (var set in sets)
        {
            set.IsFullScan = true;
        }
    }

    /// <summary>
    /// Ask before a run, once for the whole batch, when any statement is not keyed. Cancel — or no window to ask
    /// in — runs nothing.
    /// </summary>
    public async Task<ScanDecision> ConfirmRunAsync(ConnectionInfo info, ISqlDialect dialect, string sql)
    {
        if (TargetsToCheck(info, dialect, sql) is not { } targets) return ScanDecision.NotAsked;

        _status("Checking whether this reads a whole table…");
        var verdicts = await ClassifyAsync(info, targets);
        if (QuestionFor(info, verdicts) is not { } question) return ScanDecision.NotAsked;

        if (_dialogs is null)
        {
            _status("This SELECT may read a whole table and there is no window to confirm it in — nothing ran.");

            return ScanDecision.Cancelled;
        }

        if (!await _dialogs.ConfirmScanAsync(question))
        {
            _status("Cancelled — nothing ran.");

            return ScanDecision.Cancelled;
        }

        return ScanDecision.Confirmed;
    }

    /// <summary>
    /// Ask once more before Fetch all (and Export, which fetches all first) on a result confirmed as a scan that
    /// still has more to read. True = go ahead; also true when there is nothing to ask about, or nothing the
    /// fetch would do (<paramref name="info"/> null: no tab or connection — the fetch refuses on its own).
    /// </summary>
    public async Task<bool> ConfirmFetchAllAsync(ResultSetViewModel rs, ConnectionInfo? info, int fetchAllLimit)
    {
        if (!rs.IsFullScan || !rs.HasMore || info is null) return true;

        if (_dialogs is null)
        {
            _status("This result may read a whole table and there is no window to confirm it in — nothing was fetched.");

            return false;
        }

        if (!await _dialogs.ConfirmScanAsync(FetchAllQuestion(info, fetchAllLimit)))
        {
            _status("Cancelled — nothing more was read.");

            return false;
        }

        return true;
    }

    /// <summary>
    /// Look up what <see cref="ScanGuard.Classify(IEnumerable{ScanTarget}, ISchemaSnapshot?, Func{TableInfo, TableDetails?}?, Func{TableInfo, long?}?)"/>
    /// needs, within <see cref="Budget"/>, and classify with whatever arrived.
    /// </summary>
    internal async Task<IReadOnlyList<ScanVerdict>> ClassifyAsync(ConnectionInfo info, IReadOnlyList<ScanTarget> targets)
    {
        using var budget = new CancellationTokenSource(Budget);
        var details = new Dictionary<long, TableDetails>();
        var counts = new Dictionary<long, long?>();
        // The cached table list is the fallback when no session can be had in time: a DynamoDB key schema
        // cannot change after the table is created, so last time's answer about a table's partition key holds.
        var snapshot = _sessions.TryGetSnapshot(info.Id, info.Database);

        try
        {
            // Not cancelled by the budget, only no longer waited for: the run joins the same connect and the
            // same schema load, and cancelling either would make the run start them again.
            var session = await _sessions.GetOrConnectAsync(info, CancellationToken.None).WaitAsync(budget.Token);
            using var lease = _sessions.Lease(session);
            snapshot = await _sessions.EnsureSchemaAsync(session, CancellationToken.None).WaitAsync(budget.Token)
                       ?? snapshot;

            foreach (var table in targets
                         .Where(t => t.Index is not null)
                         .Select(t => ScanGuard.TableOf(t, snapshot))
                         .OfType<TableInfo>()
                         .DistinctBy(t => t.Id))
            {
                details[table.Id] = await session.Metadata.GetTableDetailsAsync(table.Id, budget.Token);
            }

            // Item counts only matter beside a scan, so they are read only when there is one to name.
            var scans = ScanGuard.Classify(targets, snapshot, Details)
                .Any(v => v.Kind == ScanVerdictKind.FullScan);
            if (scans)
            {
                foreach (var size in await session.Metadata.GetRelationSizesAsync(budget.Token))
                {
                    counts[size.TableId] = size.EstimatedRows;
                }
            }
        }
        catch (Exception)
        {
            // A connect that failed, a read that failed, or the budget running out all leave the same answer:
            // classify with what arrived. A missing fact makes a verdict Undetermined, which asks — never a
            // reason to run without asking, and never a claim that the statement scans.
        }

        return ScanGuard.Classify(targets, snapshot, Details, Counts);

        TableDetails? Details(TableInfo table) => details.GetValueOrDefault(table.Id);

        long? Counts(TableInfo table) => counts.GetValueOrDefault(table.Id);
    }
}
