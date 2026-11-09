using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit;
using Bearing.App.Controls;
using Bearing.App.Results;

namespace Bearing.App.Editing;

/// <summary>
/// The window's focus and run routes, as inline zones change them (spec docs/specs/2026-11-06-inline-results.md
/// §8): focus.results and F6 reach the caret's zone, Run and Explain with focus in a zone act on its statement,
/// and a zone's keys are never the SQL text's. Kept out of the window's composition root (§9.1).
/// </summary>
public sealed class InlineZoneFocus
{
    private readonly InlineResultsCoordinator _inline;
    private readonly TextEditor _editor;
    private readonly EditorTextCommands _text;

    public InlineZoneFocus(InlineResultsCoordinator inline, TextEditor editor, EditorTextCommands text)
    {
        _inline = inline;
        _editor = editor;
        _text = text;
    }

    /// <summary>focus.results in inline mode: the zone of the statement under the caret, else the nearest zone
    /// above it. False when zones aren't shown or there is none — the pane's grid is then the target.</summary>
    public bool FocusCaretZone()
    {
        if (!_inline.IsInline)
        {
            return false;
        }
        var caret = _editor.CaretOffset;
        var statement = _text.StatementBounds(caret);
        return _inline.FocusZoneAt(caret, statement.Start, statement.End);
    }

    /// <summary>focus.results: the caret's zone in inline mode, else <paramref name="paneGrid"/> (the open
    /// pane's grid, or null).</summary>
    /// <param name="zones">Whether zones may be tried (the window has its view model).</param>
    public void FocusResults(bool zones, Control? paneGrid)
    {
        if (!(zones && FocusCaretZone()))
        {
            paneGrid?.Focus();
        }
    }

    /// <summary>F6's results region in inline mode, as zero or one entries (<see cref="Region"/>).</summary>
    public IEnumerable<(Control Focus, Visual Container)> Regions()
        => Region() is { } region ? [region] : [];

    /// <summary>A key reaching the editor's tunnel: handled by <paramref name="handle"/> unless it came from a
    /// zone — a zone's keys are its grid's, never the SQL's (spec §8).</summary>
    public static void EditorKey(KeyEventArgs e, Func<bool> handle)
    {
        if (!InlineZoneView.IsInside(e.Source))
        {
            handle();
        }
    }

    /// <summary>
    /// F6's results region in inline mode: the focused zone, else the caret's — or null (zones not shown, or no
    /// zone with a grid). A zone sits inside the editor; <see cref="Input.FocusRing"/> classifies by the
    /// innermost container, so a focused zone is not mistaken for the editor.
    /// </summary>
    public (Control Focus, Visual Container)? Region()
    {
        if (!_inline.IsInline)
        {
            return null;
        }
        var controller = _inline.Controller;
        if (controller.FocusedZone() is { } focused
            && controller.Views.FirstOrDefault(v => v.Zone == focused) is { } current
            && current.Results.FocusableGrid is { } grid)
        {
            return (grid, current);
        }
        var statement = _text.StatementBounds(_editor.CaretOffset);
        return _inline.ZoneViewAt(_editor.CaretOffset, statement.Start, statement.End) is { } zone
            && zone.Results.FocusableGrid is { } caretGrid
            ? (caretGrid, zone)
            : null;
    }

    /// <summary>What Run covers: with focus in a zone, that zone's statement (whatever the caret is on), else
    /// the caret's statement or the selection.</summary>
    public RunScope ScopeToRun()
        => _inline.FocusedZone is { } zone ? _text.ScopeForZone(zone) : _text.ScopeToRun();

    /// <summary>What Explain covers, by the same rule as <see cref="ScopeToRun"/>.</summary>
    public string SqlToExplain()
        => _inline.FocusedZone is { } zone ? _text.ScopeForZone(zone).Sql : _text.SqlToRun();

    /// <summary>The caret is in the SQL text: focus within the editor, but not inside an inline zone.</summary>
    public bool EditorTextHasFocus(IInputElement? focused)
        => _editor.TextArea.IsKeyboardFocusWithin && !InlineZoneView.IsInside(focused);
}
