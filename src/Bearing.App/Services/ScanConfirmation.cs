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
/// <param name="Connection">Where the read lands; null only for <see cref="ScanQuestionKind.FetchAll"/> on a result
/// whose connection can no longer be resolved — still asked, without the name (fail closed).</param>
/// <param name="Kind">Which question.</param>
/// <param name="Verdicts">The statements it asks about (<see cref="ScanGuard.AsksBeforeRun"/>: not keyed, and not an
/// unfiltered scan), in batch order; empty for <see cref="ScanQuestionKind.FetchAll"/>.</param>
/// <param name="Text">The body: one sentence per statement, how rows load and what the check is.</param>
public sealed record ScanQuestion(
    ConnectionInfo? Connection,
    ScanQuestionKind Kind,
    IReadOnlyList<ScanVerdict> Verdicts,
    string Text)
{
    /// <summary>The window title. A run Bearing could not check is not called a scan here either (§1.1).</summary>
    public string Title => Kind switch
    {
        ScanQuestionKind.FetchAll => "Confirm fetch all",
        _ when IsScan => "Confirm full table scan",
        _ => "Confirm unchecked read",
    };

    /// <summary>Connection name plus its environment label, as the write confirmation names it; null when the
    /// connection is unknown.</summary>
    public string? Target => Connection is null
        ? null
        : string.IsNullOrWhiteSpace(Connection.Environment)
            ? Connection.Name
            : $"{Connection.Name} · {Connection.Environment}";

    /// <summary>
    /// The question. Says "may read a whole table" when every statement it asks about is one Bearing could not
    /// check — never "this is a scan" for those (§1.1).
    /// </summary>
    public string Heading => Kind switch
    {
        ScanQuestionKind.FetchAll => Target is null
            ? "Keep reading the whole table?"
            : $"Keep reading the whole table on {Target}?",
        _ when IsScan => $"Read a whole table on {Target}?",
        _ => $"Run on {Target}? Bearing could not check whether it reads a whole table.",
    };

    private bool IsScan => Verdicts.Any(v => v.Kind == ScanVerdictKind.FullScan);

    public string ConfirmLabel => Kind == ScanQuestionKind.Run ? "Run anyway" : "Fetch all";
}

/// <summary>What the scan check decided for one run.</summary>
/// <param name="Proceed">False when the user cancelled (or nobody could be asked): nothing runs.</param>
/// <param name="IsFullScan">The run may read a whole table — it was confirmed as a possible scan, or it holds an
/// unfiltered scan that ran unasked — so its result sets ask once more before Fetch all or Export
/// (<see cref="ResultSetViewModel.IsFullScan"/>).</param>
public readonly record struct ScanDecision(bool Proceed, bool IsFullScan)
{
    /// <summary>Nothing to ask: the setting is off, the engine has no scan question, or every read is keyed.</summary>
    public static ScanDecision NotAsked => new(Proceed: true, IsFullScan: false);

    /// <summary>Nothing asked, but the run holds an unfiltered scan (<see cref="ScanVerdict.Unfiltered"/>): its first
    /// page runs, and Fetch all / Export ask.</summary>
    public static ScanDecision UnfilteredScan => new(Proceed: true, IsFullScan: true);

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
    /// question (every engine but DynamoDB), or the batch has no <c>SELECT</c> that reads a table. Padded
    /// fail-closed by <see cref="ScanGuard.TargetsFor"/>: a read the reduction did not follow is asked about as
    /// one Bearing could not check, never run unasked.
    /// </summary>
    public static IReadOnlyList<ScanTarget>? TargetsToCheck(ConnectionInfo info, ISqlDialect dialect, string sql)
    {
        if (!info.ConfirmFullScans) return null;

        return ScanGuard.TargetsFor(dialect, sql) is { Count: > 0 } targets ? targets : null;
    }

    /// <summary>
    /// The run's question, naming only the statements that need asking (<see cref="ScanGuard.AsksBeforeRun"/>), or
    /// null when none does — every verdict keyed, or an unfiltered scan whose first page runs unasked.
    /// </summary>
    public static ScanQuestion? QuestionFor(ConnectionInfo info, IReadOnlyList<ScanVerdict> verdicts)
    {
        var asked = verdicts.Where(ScanGuard.AsksBeforeRun).ToList();
        if (ScanGuard.ConfirmationText(asked) is not { } text) return null;

        return new ScanQuestion(info, ScanQuestionKind.Run, asked, text);
    }

    /// <summary>
    /// Fetch all's second question, naming the Fetch-all row limit (<paramref name="fetchAllLimit"/>). Load more
    /// never asks again — it reads one page of the scan the user already confirmed.
    /// </summary>
    public static ScanQuestion FetchAllQuestion(ConnectionInfo? info, int fetchAllLimit)
    {
        var limit = Math.Max(1, fetchAllLimit).ToString("N0", CultureInfo.InvariantCulture);
        var text =
            "This result comes from a SELECT that may read a whole table. "
            + $"Fetch all keeps reading it until it ends or reaches the Fetch all limit of {limit} rows "
            + "(Settings ▸ Results). Export fetches all first, the same way."
            + "\n\n" + ScanGuard.Boundary;

        return new ScanQuestion(info, ScanQuestionKind.FetchAll, [], text);
    }

    /// <summary>Mark a run's result sets as coming from a possible scan (confirmed, or unfiltered and run unasked),
    /// so Fetch all / Export ask.</summary>
    public static void Mark(IEnumerable<ResultSetViewModel> sets, bool isFullScan)
    {
        if (!isFullScan) return;

        foreach (var set in sets)
        {
            set.IsFullScan = true;
        }
    }

    /// <summary>
    /// Ask before a run, once for the whole batch, when any statement needs asking — one that is not keyed, other
    /// than an unfiltered scan (<c>select * from "T"</c>, no <c>WHERE</c>), whose first page reads about one page of
    /// items and runs unasked. A run holding an unfiltered scan is still marked, so Fetch all and Export ask. Cancel
    /// — or no window to ask in — runs nothing.
    /// </summary>
    /// <param name="ct">The user's own cancel (Esc on the tab the check runs for): thrown as
    /// <see cref="OperationCanceledException"/>, never read as an answer. The lookup budget is separate.</param>
    public async Task<ScanDecision> ConfirmRunAsync(
        ConnectionInfo info, ISqlDialect dialect, string sql, CancellationToken ct = default)
    {
        if (TargetsToCheck(info, dialect, sql) is not { } targets) return ScanDecision.NotAsked;

        _status("Checking whether this reads a whole table… (Esc to stop)");
        var verdicts = await ClassifyAsync(info, targets, ct);
        ct.ThrowIfCancellationRequested();
        if (QuestionFor(info, verdicts) is not { } question)
        {
            return verdicts.Any(v => v.Unfiltered) ? ScanDecision.UnfilteredScan : ScanDecision.NotAsked;
        }

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
    /// Ask before Fetch all (and Export, which fetches all first) on a result marked as a possible scan
    /// (<see cref="Mark"/>) that still has more to read. True = go ahead; also true when there is nothing to ask about.
    /// <para>
    /// Fail closed: a scan-confirmed result asks even when <paramref name="info"/> is null (no tab, a running tab,
    /// or a connection since deleted) — the question then names no connection. Nothing reads on without an answer.
    /// </para>
    /// </summary>
    public async Task<bool> ConfirmFetchAllAsync(ResultSetViewModel rs, ConnectionInfo? info, int fetchAllLimit)
    {
        if (!rs.IsFullScan || !rs.HasMore) return true;

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
    /// Look up what the check needs, within <see cref="Budget"/>, and classify with whatever arrived — the one
    /// Sessions helper both hosts call (<see cref="ScanFacts.ClassifyAsync"/>), connecting here (bounded, never
    /// cancelling the shared connect or schema load) and naming item counts beside a scan.
    /// </summary>
    internal Task<IReadOnlyList<ScanVerdict>> ClassifyAsync(
        ConnectionInfo info, IReadOnlyList<ScanTarget> targets, CancellationToken ct = default)
        => ScanFacts.ClassifyAsync(
            _sessions, info, targets, Budget, withItemCounts: true, connected: null, ct);
}
