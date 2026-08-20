using System;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using Bearing.App.ViewModels;

namespace Bearing.App.Editing;

/// <summary>
/// Applies each tab's transient font zoom to the shared <see cref="TextEditor"/>. One editor control
/// serves every tab (the buffer is swapped on switch, see <see cref="EditorTextBehavior"/>), so the
/// zoom has to be re-applied on switch rather than bound once — which is also why the editor's
/// <c>FontSize</c> is no longer bound in XAML.
/// <para>
/// Every route into the zoom lands here: the Ctrl+= / Ctrl+- / Ctrl+0 commands and Ctrl+wheel over the
/// editor (#51). The gesture is owned by this type rather than the window so the arithmetic, the clamp and
/// the "which tab" question have one answer — and so <c>MainWindow</c> doesn't grow a pointer handler (§9.1).
/// </para>
/// <para>
/// The zoom lives on the tab and is deliberately not persisted: a reopened or new tab starts at the
/// configured base size again, and Settings ▸ Editor ▸ Font size never moves under the user's feet. That is
/// the opposite of the cell inspector, where Ctrl+wheel <em>does</em> write back to its setting — a document
/// zoom is transient, a chrome size is a preference.
/// </para>
/// </summary>
internal sealed class EditorZoomController
{
    private readonly TextEditor _editor;
    private readonly Func<double> _baseSize;   // Settings ▸ Editor ▸ font size, mirrored on the shell
    private readonly Action<double>? _report;  // says what happened; a one-point change is easy to miss
    private readonly WheelZoomAccumulator _wheel = new();
    private EditorTabViewModel? _tab;

    /// <param name="report">Called with the new size after any zoom the user asked for (not on tab switch —
    /// showing a size nobody changed would be noise).</param>
    public EditorZoomController(TextEditor editor, Func<double> baseSize, Action<double>? report = null)
    {
        _editor = editor;
        _baseSize = baseSize;
        _report = report;
        // Tunnelling, so the editor's own ScrollViewer doesn't scroll the document instead of zooming —
        // the same reason CellInspectorView tunnels its wheel handler.
        editor.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheel, RoutingStrategies.Tunnel);
    }

    /// <summary>Show <paramref name="tab"/>'s zoom (call on tab switch; null falls back to the base size).</summary>
    public void Bind(EditorTabViewModel? tab)
    {
        _tab = tab;
        Apply();
    }

    /// <summary>Re-apply after the base size changed in settings, keeping the tab's own offset.</summary>
    public void Refresh() => Apply();

    /// <summary>Ctrl+= / Ctrl+- — one point per press on the selected tab only.</summary>
    public void ZoomIn() => Zoom(+1);
    public void ZoomOut() => Zoom(-1);

    /// <summary>Ctrl+0 — back to the configured base size for this tab.</summary>
    public void Reset()
    {
        if (_tab is { } tab) tab.FontZoomSteps = 0;
        Apply();
        _report?.Invoke(CurrentSize);
    }

    /// <summary>The size now on screen — for the status line after a zoom command.</summary>
    public double CurrentSize => EditorZoom.SizeFor(_baseSize(), _tab?.FontZoomSteps ?? 0);

    /// <summary>
    /// Ctrl+wheel zooms the current tab, a point per notch, exactly as the keyboard commands do — it does
    /// not touch the configured base size. The event is claimed as soon as Ctrl is down, even while the
    /// accumulator is still short of a whole notch: a trackpad swipe would otherwise scroll the document
    /// on the way to its first step.
    /// </summary>
    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;
        e.Handled = true;
        if (_wheel.Add(e.Delta.Y) is var steps && steps != 0) Zoom(steps);
    }

    /// <summary>Move the selected tab by <paramref name="steps"/> points and report the result once,
    /// however many notches the gesture released.</summary>
    private void Zoom(int steps)
    {
        if (_tab is not { } tab) return;   // no tab: nothing to zoom, and nowhere to remember it
        // One step at a time even for a multi-notch gesture: EditorZoom.Nudge refuses a move that would
        // leave the legible range, so asking for +3 near the ceiling would do nothing at all instead of
        // travelling the remaining point.
        var direction = Math.Sign(steps);
        for (var i = 0; i < Math.Abs(steps); i++)
            tab.FontZoomSteps = EditorZoom.Nudge(_baseSize(), tab.FontZoomSteps, direction);
        Apply();
        _report?.Invoke(CurrentSize);
    }

    private void Apply() => _editor.FontSize = EditorZoom.SizeFor(_baseSize(), _tab?.FontZoomSteps ?? 0);
}
