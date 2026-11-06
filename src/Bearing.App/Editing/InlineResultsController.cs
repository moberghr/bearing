using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Bearing.App.Controls;
using Bearing.App.Results;
using Bearing.App.ViewModels;

namespace Bearing.App.Editing;

/// <summary>
/// Shows the bound tab's <see cref="EditorTabViewModel.InlineZones"/> inside the editor, each on its own row
/// beneath the line it hangs under (spec: docs/specs/2026-11-06-inline-results.md).
/// <para>
/// <b>Mechanism.</b> AvaloniaEdit's height tree is internal, so the one supported way to make room between
/// two lines is an inline object: a zero-length element at the line's end that emits a
/// <see cref="TextEndOfLine"/> (ending the text row) and then an <see cref="InlineObjectRun"/> hosting the
/// control — the control gets a row of its own inside that visual line and the lines below move down.
/// </para>
/// <para>
/// <b>Anchoring.</b> One editor serves every tab and its text is replaced on a switch, so a zone's position
/// lives on the tab (<see cref="InlineResultZone.Offset"/>). While the tab is shown, a
/// <see cref="TextAnchor"/> per zone follows the edits and writes the offset back after each change; deleting
/// the text a zone hangs under removes the zone. <see cref="Unbind"/> must run <i>before</i> the buffer is
/// replaced, or the swap would read as every anchor's text being deleted.
/// </para>
/// </summary>
public sealed class InlineResultsController
{
    private readonly TextEditor _editor;
    private readonly Func<bool> _enabled;
    private readonly Func<EditorTabViewModel, InlineResultZone, InlineZoneView> _createView;
    private readonly Dictionary<string, StackPanel> _hosts = new();   // per line's zone set, reused across rebuilds
    private readonly ZoneGenerator _generator;
    private readonly Dictionary<Guid, Shown> _shown = new();
    // A zone's view lives as long as the zone (spec §10): scrolled off-screen, or its tab switched away and
    // back, it comes back as it was left — scroll, selection, inspector, Back stack. Keyed weakly, so a zone
    // that is closed, replaced by a run or dropped with its tab takes its view with it.
    private readonly ConditionalWeakTable<InlineResultZone, InlineZoneView> _views = new();
    private EditorTabViewModel? _tab;
    private int _caretLine = 1;
    private bool _inCaretHandler;
    private TextViewPosition? _pendingStep;
    private bool _steppingCaret;
    private TextDocument? _document;
    private int _bindGeneration;   // bumped on every Bind: a run that spans one cannot map its offsets
    private int _lastChangeOffset;  // where the latest edit happened — where a kept zone lands (spec §3)

    /// <summary>A zone of the bound tab: its anchor always (the offset must follow edits in either mode), its
    /// control only once it is drawn — in Pane mode a run's zones are placed but never shown, and building
    /// a results view for each would be work on the UI thread for nothing.</summary>
    private sealed class Shown(InlineResultZone zone, TextAnchor anchor)
    {
        public InlineResultZone Zone { get; } = zone;
        public TextAnchor Anchor { get; set; } = anchor;
    }

    /// <param name="enabled">Whether results are placed inline right now (the setting), read live.</param>
    /// <param name="createView">Build a zone's view, wired to its tab (called once per zone, on first draw).</param>
    public InlineResultsController(TextEditor editor, Func<bool> enabled,
        Func<EditorTabViewModel, InlineResultZone, InlineZoneView> createView)
    {
        _editor = editor;
        _enabled = enabled;
        _createView = createView;
        _generator = new ZoneGenerator(this);
        TextView.ElementGenerators.Add(_generator);
        TextView.SizeChanged += (_, _) => FitWidths();
        TextView.ScrollOffsetChanged += (_, _) => PinHosts();
        _editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
    }

    private TextView TextView => _editor.TextArea.TextView;

    /// <summary>The tab whose zones are shown, or null.</summary>
    public EditorTabViewModel? Tab => _tab;

    /// <summary>The zone views built so far for the bound tab's zones (built on first draw, kept with the zone).</summary>
    internal IReadOnlyCollection<InlineZoneView> Views
        => _shown.Values.Select(s => _views.TryGetValue(s.Zone, out var v) ? v : null).OfType<InlineZoneView>().ToList();

    /// <summary>Every zone view still alive, on any tab — kept per zone across tab switches (spec §10).</summary>
    internal IReadOnlyList<InlineZoneView> AllViews => _views.Select(kv => kv.Value).ToList();

    /// <summary>The zone whose view holds keyboard focus, or null.</summary>
    public InlineResultZone? FocusedZone()
        => Views.FirstOrDefault(v => v.IsKeyboardFocusWithin)?.Zone;

    /// <summary>Start showing <paramref name="tab"/>'s zones over the buffer now in the editor. Call after the
    /// tab's text has been loaded.</summary>
    public void Bind(EditorTabViewModel? tab)
    {
        Unbind();
        _bindGeneration++;
        _tab = tab;
        if (tab is null)
        {
            return;
        }
        _document = _editor.Document;
        _document.Changing += OnDocumentChanging;
        _document.Changed += OnDocumentChanged;
        tab.InlineZonesChanged += Sync;
        Sync();
    }

    /// <summary>Stop showing the current tab's zones, leaving their offsets on the tab. Call before the
    /// editor's buffer is replaced.</summary>
    public void Unbind()
    {
        if (_tab is not null)
        {
            _tab.InlineZonesChanged -= Sync;
        }
        if (_document is not null)
        {
            _document.Changing -= OnDocumentChanging;
            _document.Changed -= OnDocumentChanged;
        }
        foreach (var s in _shown.Values)
        {
            s.Anchor.Deleted -= OnAnchorDeleted;
        }
        _shown.Clear();
        foreach (var host in _hosts.Values)
        {
            host.Children.Clear();
        }
        _hosts.Clear();
        _tab = null;
        _document = null;
        TextView.Redraw();
    }

    /// <summary>What a run started from: compared after it finishes to decide whether, and where, its results
    /// can be placed.</summary>
    public sealed record RunStart(EditorTabViewModel Tab, string Text, int RunResultsStamp,
        ITextSourceVersion? Version, int BindGeneration);

    /// <summary>The outcome of <see cref="PlaceRun"/>.</summary>
    public enum Placement
    {
        /// <summary>The run produced no new results (refused, cancelled, failed to connect).</summary>
        Nothing,

        /// <summary>Placed under their statements.</summary>
        Inline,

        /// <summary>New results, but the text moved in a way that can't be traced back to where the run was
        /// (the tab was switched or reloaded during it). Shown in the pane rather than guessed at.</summary>
        Untraceable,
    }

    /// <summary>Capture what a run on <paramref name="tab"/> starts from. Call right before executing.</summary>
    public RunStart BeginRun(EditorTabViewModel tab)
        => new(tab, tab.Text, tab.RunResultsStamp, ReferenceEquals(tab, _tab) ? _document?.Version : null, _bindGeneration);

    /// <summary>
    /// After a run over <paramref name="scope"/>: place its results inline, under where its statements are
    /// <i>now</i>. The buffer stays editable while a query runs, so the offsets captured at the start are
    /// moved through the edits made since (the document's version history). A tab switch or reload in between
    /// replaces the whole text — offsets can't be carried across that, so the results go to the pane instead.
    /// </summary>
    public Placement PlaceRun(RunScope scope, RunStart start)
    {
        var tab = start.Tab;
        // The run says whether it set results (the stamp), rather than this guessing from whether the tab's
        // results changed: FK navigation, ⤢ and Back can swap them while the run is still before its
        // statement — counting rows, waiting on the write confirmation — and a declined write sets nothing.
        if (tab.RunResultsStamp == start.RunResultsStamp || tab.RunResults.Count == 0)
        {
            return Placement.Nothing;
        }
        if (Rebase(scope, start) is not { } now)
        {
            return Placement.Untraceable;
        }
        tab.ApplyInlineRun(now, Results.InlineResultPlacement.Assign(now, tab.RunResults));
        return Placement.Inline;
    }

    private RunScope? Rebase(RunScope scope, RunStart start)
    {
        if (string.Equals(start.Tab.Text, start.Text, StringComparison.Ordinal))
        {
            return scope;
        }
        if (!ReferenceEquals(start.Tab, _tab) || start.BindGeneration != _bindGeneration
            || start.Version is not { } then || _document?.Version is not { } now || !then.BelongsToSameDocumentAs(now))
        {
            return null;
        }
        int Move(int offset) => then.MoveOffsetTo(now, offset, AnchorMovementType.BeforeInsertion);
        return scope with
        {
            StatementEnds = scope.StatementEnds.Select(Move).ToList(),
            Start = Move(scope.Start),
            End = Move(scope.End),
        };
    }

    /// <summary>Re-read the setting (shown ↔ hidden). Zones stay on their tab either way.</summary>
    public void Refresh() => TextView.Redraw();

    /// <summary>Bring the realised controls in line with the tab's zone list.</summary>
    private void Sync()
    {
        if (_tab is not { } tab || _document is not { } document)
        {
            return;
        }
        var wanted = tab.InlineZones.ToDictionary(z => z.Id);
        // Hosts are keyed by a line's zone set; drop the ones naming a zone that is gone.
        foreach (var stale in _hosts.Keys.Where(k => k.Split(',').Any(id => !wanted.ContainsKey(Guid.Parse(id)))).ToList())
        {
            _hosts[stale].Children.Clear();
            _hosts.Remove(stale);
        }
        foreach (var gone in _shown.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
        {
            _shown[gone].Anchor.Deleted -= OnAnchorDeleted;
            _shown.Remove(gone);
        }
        foreach (var zone in tab.InlineZones)
        {
            if (_shown.ContainsKey(zone.Id))
            {
                continue;
            }
            // The run finished after the buffer moved on (or the tab's text was reloaded): clamp rather than
            // throw. The zone then hangs under whatever line the offset now falls on.
            _shown[zone.Id] = new Shown(zone, Anchor(document, zone));
        }
        FitWidths();
        TextView.Redraw();
    }

    private TextAnchor Anchor(TextDocument document, InlineResultZone zone)
    {
        zone.Offset = Math.Clamp(zone.Offset, 0, document.TextLength);
        var anchor = document.CreateAnchor(zone.Offset);
        anchor.MovementType = AnchorMovementType.BeforeInsertion;
        anchor.SurviveDeletion = false;
        anchor.Deleted += OnAnchorDeleted;
        return anchor;
    }

    private void OnDocumentChanging(object? sender, DocumentChangeEventArgs e) => _lastChangeOffset = e.Offset;

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        foreach (var s in _shown.Values)
        {
            if (!s.Anchor.IsDeleted)
            {
                s.Zone.Offset = s.Anchor.Offset;
            }
        }
    }

    /// <summary>The text a zone hung under was deleted. A zone without unsaved edits goes with it; one holding
    /// edits is kept where the text was, marked "statement removed" (spec §3) — deleting text can't stop to ask,
    /// and dropping the zone would lose the edits. Done after the document change has finished, not inside
    /// it: the text view is mid-update while anchors are being notified.</summary>
    private void OnAnchorDeleted(object? sender, EventArgs e)
    {
        var tab = _tab;
        var dead = _shown.Values.Where(s => ReferenceEquals(s.Anchor, sender)).ToList();
        if (tab is null || dead.Count == 0)
        {
            return;
        }
        var at = _lastChangeOffset;
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var shown in dead)
            {
                if (shown.Zone.PendingCount == 0 || _document is not { } document || !ReferenceEquals(tab, _tab))
                {
                    tab.RemoveInlineZone(shown.Zone.Id);
                    continue;
                }
                shown.Anchor.Deleted -= OnAnchorDeleted;
                shown.Zone.Offset = at;
                shown.Anchor = Anchor(document, shown.Zone);
                shown.Zone.StatementRemoved = true;
                shown.Zone.NotifyChanged();
                TextView.Redraw();
            }
        }, DispatcherPriority.Normal);
    }

    /// <summary>Inline objects are measured with infinite space, so each zone is given an explicit width
    /// limit: the visible text area, less a little room so it doesn't touch the edge. Within it the zone fits
    /// its columns (spec §6).</summary>
    private void FitWidths()
    {
        foreach (var view in Views)
        {
            view.MaxWidth = ZoneWidth;
        }
    }

    private double ZoneWidth => Math.Max(160, TextView.Bounds.Width - 24);

    /// <summary>Keep every zone at the viewport's left edge while the editor scrolls sideways (spec §6): the
    /// zone sits in the text, which slides left, and would take its first columns and buttons with it.</summary>
    private void PinHosts()
    {
        foreach (var host in _hosts.Values)
        {
            Pin(host);
        }
    }

    private void Pin(Control host)
        => host.RenderTransform = TextView.HorizontalOffset > 0 ? new TranslateTransform(TextView.HorizontalOffset, 0) : null;

    private InlineZoneView ViewFor(Shown shown)
    {
        if (_views.TryGetValue(shown.Zone, out var built))
        {
            return built;
        }
        var view = _createView(_tab!, shown.Zone);
        // A taller or shorter zone changes the inline object's size; the text view re-measures that line on
        // its next measure pass. A grip drag says so; rows arriving (paging, an added row) resize it too.
        view.HeightChanged += () => TextView.InvalidateMeasure();
        view.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) >= 0.5)
            {
                TextView.InvalidateMeasure();
            }
        };
        view.MaxWidth = ZoneWidth;
        _views.Add(shown.Zone, view);
        return view;
    }

    /// <summary>
    /// Give <paramref name="zone"/>'s grid the keyboard (spec §8): scroll its line into view so the zone is
    /// realized, then focus its first grid. False when the zone has no grid to focus (a message, an error) or
    /// isn't on the bound tab.
    /// </summary>
    public bool Focus(InlineResultZone zone)
    {
        if (_document is not { } document || !_shown.TryGetValue(zone.Id, out var shown) || shown.Anchor.IsDeleted)
        {
            return false;
        }
        var line = document.GetLineByOffset(Math.Clamp(zone.Offset, 0, document.TextLength)).LineNumber;
        _editor.ScrollToLine(line);
        _editor.UpdateLayout();
        var view = ViewFor(shown);
        return view.Results.FocusableGrid?.Focus() == true;
    }

    /// <summary>Esc out of a zone: the editor gets the keyboard, the caret at the end of the zone's statement.</summary>
    public void Leave(InlineResultZone zone)
    {
        if (_document is { } document)
        {
            _editor.CaretOffset = Math.Clamp(zone.Offset, 0, document.TextLength);
        }
        _editor.TextArea.Focus();
    }

    /// <summary>
    /// A zone's row belongs to its statement line's visual line, and it has no caret stops, so Up/Down onto it
    /// snap back to the end of the statement line — and Down from there targets the zone row again, so the
    /// caret could never get past a zone. Step over it: a vertical move that lands at the zone (Down from the
    /// statement line, Up from the line below) continues to the next line, or back onto the statement's text,
    /// at the caret's desired x.
    /// <para>
    /// A vertical move is told apart by <see cref="Caret.DesiredXPos"/>: only Up/Down set it, and every other
    /// move (End, typing, a click, Left/Right) resets it to NaN. AvaloniaEdit assigns it right <i>after</i> the
    /// position, hence the posted check. Reacting to the move instead of to keys keeps §9.2's single input
    /// pipeline intact.
    /// </para>
    /// </summary>
    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        // Reading the caret's visual column revalidates it, which raises this event again from inside itself;
        // the nested call is the same landing and would record the wrong "from" line, so it is ignored.
        if (_inCaretHandler || _steppingCaret)
        {
            return;
        }
        var caret = _editor.TextArea.Caret;
        _inCaretHandler = true;
        var fromLine = _caretLine;
        try
        {
            if (_document is null || !TextView.VisualLinesValid
                || TextView.GetVisualLine(caret.Line) is not { } visual
                || visual.Elements.OfType<ZoneElement>().FirstOrDefault() is not { } zone
                || caret.VisualColumn < zone.VisualColumn)
            {
                return;
            }
            var landed = caret.Position;
            if (_pendingStep == landed)
            {
                return;   // already queued for this landing, with the right "from" line
            }
            _pendingStep = landed;
            var zoneLine = visual.LastDocumentLine.LineNumber;
            Dispatcher.UIThread.Post(() =>
            {
                _pendingStep = null;
                StepOverZone(landed, fromLine, zoneLine);
            }, DispatcherPriority.Normal);
        }
        finally
        {
            _caretLine = caret.Line;
            _inCaretHandler = false;
        }
    }

    private void StepOverZone(TextViewPosition landed, int fromLine, int zoneLine)
    {
        var caret = _editor.TextArea.Caret;
        var x = caret.DesiredXPos;
        if (_document is not { } document || double.IsNaN(x) || caret.Position != landed)
        {
            return;
        }
        // Down from the statement line itself, or Up from below. Arriving from above lands at the end of a
        // statement line shorter than the caret's x — a real text position, left alone.
        if (fromLine < zoneLine)
        {
            return;
        }
        var down = fromLine == zoneLine;
        if (down && zoneLine >= document.LineCount)
        {
            return;   // nothing below: the end of the last line is as far as Down goes
        }
        var target = TextView.GetOrConstructVisualLine(document.GetLineByNumber(down ? zoneLine + 1 : zoneLine));
        var row = target.TextLines[0];   // the text row: the next line's only one, or the statement line's first
        var column = target.GetVisualColumn(new Avalonia.Point(x, target.GetTextLineVisualYPosition(row, VisualYPosition.LineMiddle)), false);
        _steppingCaret = true;
        try
        {
            caret.Position = target.GetTextViewPosition(Math.Min(column, target.GetTextLineVisualStartColumn(row) + row.Length - 1));
            caret.DesiredXPos = x;
        }
        finally
        {
            _steppingCaret = false;
            _caretLine = caret.Line;
        }
    }

    /// <summary>The zones that hang under document line <paramref name="lineNumber"/>, in order.</summary>
    private List<Shown> ZonesOnLine(TextDocument document, int lineNumber)
    {
        if (!_enabled())
        {
            return [];
        }
        return _shown.Values
            .Where(s => !s.Anchor.IsDeleted && s.Anchor.Line == lineNumber && ReferenceEquals(document, _document))
            .OrderBy(s => s.Anchor.Offset)
            .ToList();
    }

    /// <summary>Asks for the end of every visual line that has zones hanging under its last document line.</summary>
    private sealed class ZoneGenerator(InlineResultsController owner) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset)
        {
            var line = CurrentContext.VisualLine.LastDocumentLine;
            if (startOffset > line.EndOffset || owner._shown.Count == 0)
            {
                return -1;
            }
            return owner.ZonesOnLine(CurrentContext.Document, line.LineNumber).Count > 0 ? line.EndOffset : -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var line = CurrentContext.Document.GetLineByOffset(offset);
            var zones = owner.ZonesOnLine(CurrentContext.Document, line.LineNumber);
            if (zones.Count == 0)
            {
                return null;
            }
            var views = zones.Select(owner.ViewFor).ToList();
            // One element per line (the line builder asks a generator once at the line end), so several zones
            // on one line share a host. Detach each from a previous host first: a visual line is rebuilt on
            // every edit and scroll, and a control can have only one parent.
            // Reused while the line's zones are the same ones: a visual line is rebuilt on every edit, scroll and
            // re-measure, and re-parenting the controls each time would drop a resize drag's pointer capture.
            var key = string.Join(",", views.Select(v => v.Zone.Id));
            if (owner._hosts.TryGetValue(key, out var cached) && cached.Children.SequenceEqual(views))
            {
                return new ZoneElement(cached);
            }
            var host = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Left };
            owner._hosts[key] = host;
            owner.Pin(host);
            foreach (var view in views)
            {
                if (view.Parent is Panel previous)
                {
                    previous.Children.Remove(view);
                }
                host.Children.Add(view);
            }
            return new ZoneElement(host);
        }
    }

    /// <summary>Zero document length; two visual columns — the forced line break, then the hosted control.
    /// No caret stops inside it.</summary>
    private sealed class ZoneElement(Control host) : VisualLineElement(2, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            if (startVisualColumn == VisualColumn)
            {
                return new TextEndOfLine(1);
            }
            return new InlineObjectRun(1, TextRunProperties, host);
        }

        public override bool HandlesLineBorders => true;

        public override int GetNextCaretPosition(int visualColumn, AvaloniaEdit.Document.LogicalDirection direction, CaretPositioningMode mode)
            => -1;
    }
}
