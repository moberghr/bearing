using System;
using AvaloniaEdit;
using Bearing.App.ViewModels;

namespace Bearing.App.Editing;

/// <summary>
/// Applies each tab's transient font zoom to the shared <see cref="TextEditor"/>. One editor control
/// serves every tab (the buffer is swapped on switch, see <see cref="EditorTextBehavior"/>), so the
/// zoom has to be re-applied on switch rather than bound once — which is also why the editor's
/// <c>FontSize</c> is no longer bound in XAML.
/// <para>
/// The zoom lives on the tab and is deliberately not persisted: a reopened or new tab starts at the
/// configured base size again.
/// </para>
/// </summary>
internal sealed class EditorZoomController
{
    private readonly TextEditor _editor;
    private readonly Func<double> _baseSize;   // Settings ▸ Editor ▸ font size, mirrored on the shell
    private EditorTabViewModel? _tab;

    public EditorZoomController(TextEditor editor, Func<double> baseSize)
    {
        _editor = editor;
        _baseSize = baseSize;
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
    public void ZoomIn() => Nudge(+1);
    public void ZoomOut() => Nudge(-1);

    /// <summary>Ctrl+0 — back to the configured base size for this tab.</summary>
    public void Reset()
    {
        if (_tab is { } tab) tab.FontZoomSteps = 0;
        Apply();
    }

    /// <summary>The size now on screen — for the status line after a zoom command.</summary>
    public double CurrentSize => EditorZoom.SizeFor(_baseSize(), _tab?.FontZoomSteps ?? 0);

    private void Nudge(int delta)
    {
        if (_tab is not { } tab) return;   // no tab: nothing to zoom, and nowhere to remember it
        tab.FontZoomSteps = EditorZoom.Nudge(_baseSize(), tab.FontZoomSteps, delta);
        Apply();
    }

    private void Apply() => _editor.FontSize = EditorZoom.SizeFor(_baseSize(), _tab?.FontZoomSteps ?? 0);
}
