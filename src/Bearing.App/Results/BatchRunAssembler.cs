using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Sessions;
using Bearing.Sql;

namespace Bearing.App.Results;

/// <summary>
/// Builds a batch run's result sets from <see cref="IQueryExecutor.ExecuteStreamingAsync"/> as they arrive, so
/// each grid shows up when its first rows do and fills in behind them (§5.8). A batch is still read once and
/// never pages; this changes when its rows become visible, not which rows are read.
/// <para>
/// No connection and no UI: it is fed the events and hands each new list of sets to <c>publish</c>, which the
/// caller points at the tab. What it settles is the bookkeeping that has to be right for the grids to tell the
/// truth — which set is still being read, which one an error or Esc cut off, and where the messages and errors
/// that have no grid go once the run is over.
/// </para>
/// </summary>
internal sealed class BatchRunAssembler
{
    private readonly string _sql;
    private readonly ProviderTraits _traits;
    private readonly bool _readOnly;
    private readonly PreparedRun? _run;
    private readonly Action<IReadOnlyList<ResultSetViewModel>, bool> _publish;
    private readonly Func<TimeSpan> _elapsed;
    private readonly string? _wholeRun;

    /// <summary>Sets that returned rows, by their index in the run's results.</summary>
    private readonly SortedDictionary<int, ResultSetViewModel> _sets = new();

    /// <summary>The set being read now; null between sets.</summary>
    private int? _reading;

    /// <summary>
    /// How often rows read for the current set are handed to its grid. Not per batch: each hand-over makes the
    /// grid rebuild its view of the whole set (<see cref="RowCollection"/>), and a fast set's batches arrive
    /// quicker than that — handing each one over kept the UI thread busy until the set ended (measured: the app
    /// stopped responding for seconds on a 150,000-row set). A few times a second still reads as live.
    /// </summary>
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Rows read for the current set and not yet on its grid.</summary>
    private readonly List<object?[]> _pending = new();
    private TimeSpan _lastFlush;

    /// <param name="publish">Gets every new list of sets, with true the first time (a run's first results
    /// replace the pane; later ones extend the same run).</param>
    /// <param name="elapsed">The run's time so far — what a set that ends shows until the run reports its
    /// own.</param>
    public BatchRunAssembler(
        string sql, ProviderTraits traits, bool connectionReadOnly, PreparedRun? run,
        Action<IReadOnlyList<ResultSetViewModel>, bool> publish, Func<TimeSpan> elapsed)
    {
        _sql = sql;
        _traits = traits;
        _readOnly = connectionReadOnly;
        _run = run;
        _publish = publish;
        _elapsed = elapsed;
        // Until the run says which statement each set came from, every set reports the whole run's text — the
        // same honest fallback ResultSetBuilder uses when it can't prove a mapping.
        var placeholder = new QueryResult([], [], 0, TimeSpan.Zero, null, null, false);
        _wholeRun = ResultSetBuilder.ExecutedStatements([placeholder], sql, run)[0].Shown;
    }

    /// <summary>Sets published so far.</summary>
    public int Started => _sets.Count;

    /// <summary>The set being read now, for progress text; null between sets.</summary>
    public ResultSetViewModel? Reading => _reading is { } i ? _sets[i] : null;

    /// <summary>A set has begun: build it — editability, keys and FKs resolve from its columns now, so the grid
    /// is the same one it would have been at the end — and publish it.</summary>
    public void Start(ResultStarted started, ISchemaSnapshot? snapshot)
    {
        Flush();
        if (_reading is { } open) _sets[open].EndReading(more: false, _elapsed());
        var shape = new QueryResult(started.Columns, [], 0, TimeSpan.Zero, null, null, false);
        var vm = ResultSetBuilder.BuildOne(shape, _sql, pageable: false, _wholeRun, snapshot, _traits, _readOnly, _run);
        vm.IsReading = true;
        _sets[started.Index] = vm;
        _reading = started.Index;
        _publish(_sets.Values.ToList(), _sets.Count == 1);
    }

    /// <summary>
    /// Rows for the set being read. They reach its grid at most every <see cref="FlushInterval"/> — and straight
    /// away when the grid is still empty, so a set's first rows show the moment they arrive. True when this call
    /// handed rows over, which is the caller's cue to let the UI catch up.
    /// </summary>
    public bool Rows(ResultRows rows)
    {
        if (rows.Index != _reading) return false;
        _pending.AddRange(rows.Rows);
        var empty = _sets[rows.Index].Loaded == 0;
        if (!empty && _elapsed() - _lastFlush < FlushInterval) return false;
        Flush();
        return true;
    }

    /// <summary>Rows read so far for the current set but not yet shown — counted into progress text.</summary>
    public int Pending => _pending.Count;

    /// <summary>Hand every pending row to the current set's grid.</summary>
    public void Flush()
    {
        _lastFlush = _elapsed();
        if (_pending.Count == 0 || _reading is not { } i) { _pending.Clear(); return; }
        _sets[i].AppendPage(_pending.ToArray(), hasMore: false);
        _pending.Clear();
    }

    public void End(ResultEnded ended)
    {
        Flush();
        if (!_sets.TryGetValue(ended.Index, out var vm)) return;
        vm.EndReading(more: ended.Truncated, _elapsed());
        if (_reading == ended.Index) _reading = null;
    }

    /// <summary>
    /// Esc or a thrown failure: the set being read keeps its rows and is marked cut short. Every other set
    /// already ended, so it stands as it is.
    /// </summary>
    public void Abandon()
    {
        Flush();   // rows already read are kept, shown or not
        if (_reading is not { } i) return;
        _sets[i].EndReading(more: true, _elapsed());
        _reading = null;
    }

    /// <summary>
    /// The run is over: give each set its own statement and time, and add the results that have no grid —
    /// counts, messages and the error, which follows the set it cut off. Publishes the final list when it
    /// differs from what is on screen, and returns it.
    /// </summary>
    public IReadOnlyList<ResultSetViewModel> Complete(IReadOnlyList<QueryResult> results, ISchemaSnapshot? snapshot)
    {
        Abandon(); // a set still open here was cut off by the failure that ended the run
        var executed = ResultSetBuilder.ExecutedStatements(results, _sql, _run);
        var final = new List<ResultSetViewModel>(results.Count);
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (_sets.TryGetValue(i, out var vm))
            {
                vm.ExecutedSql = executed[i].Shown;
                // Known only now (Postgres proves it over the whole batch); inline placement reads it.
                vm.StatementIndex = result.Success ? result.StatementIndex : null;
                if (result.Success) vm.EndReading(vm.HasMore, result.Duration);
                final.Add(vm);
                if (result.Success) continue;
                // The failure is result i too: the set it interrupted, then the error that interrupted it.
            }
            final.Add(ResultSetBuilder.BuildOne(
                result, executed[i].Source, pageable: false, executed[i].Shown, snapshot, _traits, _readOnly, _run));
        }

        if (final.Count != _sets.Count) _publish(final, _sets.Count == 0);
        return final;
    }
}
