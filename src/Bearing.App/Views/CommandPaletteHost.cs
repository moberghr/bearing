using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Bearing.App.Controls;
using Bearing.App.Input;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Views;

/// <summary>
/// Owns the shell's two filterable overlays: the command palette (Ctrl+Shift+P — every runnable command,
/// fuzzy-ranked, with its current gesture) and the generic quick-pick used for project / connection /
/// database selection. Both are instances of <see cref="FilterableListOverlay{T}"/>; this class supplies each
/// one's rows, row template and pick action, and enforces that only one is ever open.
/// <para>
/// Overlays own the keyboard while up, so the window suppresses global shortcuts whenever
/// <see cref="AnyOpen"/> is true — see <c>MainWindow.OnKeyDown</c>.
/// </para>
/// </summary>
public sealed class CommandPaletteHost
{
    private readonly Window _owner;
    private readonly CommandRegistry _commands;
    private readonly Func<Keymap> _keymap;

    private FilterableListOverlay<PaletteRow>? _palette;
    private FilterableListOverlay<QuickPickRow>? _quickPick;

    /// <param name="keymap">Reads the live keymap: gestures shown in the palette must follow a rebind made in
    /// the shortcuts editor.</param>
    public CommandPaletteHost(Window owner, CommandRegistry commands, Func<Keymap> keymap)
    {
        _owner = owner;
        _commands = commands;
        _keymap = keymap;
    }

    /// <summary>Whether either overlay is up (i.e. something else owns the keyboard).</summary>
    public bool AnyOpen => _palette?.IsOpen == true || _quickPick?.IsOpen == true;

    /// <summary>Dismiss the topmost open overlay, most-modal first. Returns false when neither is open, so
    /// Escape can fall through to the next thing that wants it.</summary>
    public bool HideTopmost()
    {
        if (_quickPick?.IsOpen == true) { _quickPick.Hide(); return true; }
        if (_palette?.IsOpen == true) { _palette.Hide(); return true; }
        return false;
    }

    /// <summary>palette.open: a fuzzy-searchable list of every applicable command with its current gesture.
    /// Re-invoking while open closes it (toggle).</summary>
    public void TogglePalette()
    {
        if (_palette?.IsOpen == true) { _palette.Hide(); return; }
        _palette = new FilterableListOverlay<PaletteRow>(
            _owner, "Type a command…", 560,
            new FuncDataTemplate<PaletteRow>((row, _) => BuildPaletteRow(row), supportsRecycling: true),
            query: PaletteRows,
            onPick: row => { if (row.Command.CanRun()) CrashReporter.Observe(row.Command.Run(), $"command '{row.Command.Id}'"); });
        _palette.Show();
    }

    /// <summary>Open a single filterable quick-pick (project / connection / database). Opening one replaces
    /// any other overlay, so only one picker is ever active. An empty item list is a no-op.</summary>
    public void ShowQuickPick(string placeholder, IReadOnlyList<(string Label, Action Pick)> items)
    {
        if (items.Count == 0) return;
        _palette?.Hide();
        _quickPick?.Hide();
        _quickPick = new FilterableListOverlay<QuickPickRow>(
            _owner, placeholder, 460,
            new FuncDataTemplate<QuickPickRow>((row, _) =>
                new TextBlock { Text = row.Label, Margin = new Thickness(4, 2), Foreground = Res("Text.Primary") },
                supportsRecycling: true),
            query: q => QuickPickRows(items, q),
            onPick: row => row.Pick());
        _quickPick.Show();
    }

    /// <summary>Rank every runnable command against the query, pairing each with its current gesture text.</summary>
    private IReadOnlyList<PaletteRow> PaletteRows(string query)
        => PaletteFilter.Rank(_commands.All.Where(c => c.CanRun()), query)
            .Select(c => new PaletteRow(c, _keymap().DisplayGesture(c.Id)))
            .ToList();

    /// <summary>Filter the fixed item list by fuzzy score (all items when the query is blank).</summary>
    private static IReadOnlyList<QuickPickRow> QuickPickRows(IReadOnlyList<(string Label, Action Pick)> items, string query)
    {
        IEnumerable<(string Label, Action Pick)> filtered = string.IsNullOrWhiteSpace(query)
            ? items
            : items
                .Select(x => (x, score: PaletteFilter.Score(x.Label, query.Trim())))
                .Where(t => t.score.HasValue)
                .OrderByDescending(t => t.score!.Value)
                .Select(t => t.x);
        return filtered.Select(x => new QuickPickRow(x.Label, x.Pick)).ToList();
    }

    private sealed record QuickPickRow(string Label, Action Pick);

    /// <summary>A palette row: the command plus its current gesture text (may be null when unbound).</summary>
    private sealed record PaletteRow(KeyCommand Command, string? Gesture);

    private static Control BuildPaletteRow(PaletteRow row)
    {
        var title = new TextBlock { Text = row.Command.Title, VerticalAlignment = VerticalAlignment.Center };
        var group = new TextBlock
        {
            Text = row.Command.Group,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("Text.Faint"),
            FontSize = 11,
        };
        var gesture = new TextBlock
        {
            Text = row.Gesture ?? "",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("Text.Dim"),
            FontSize = 11,
        };
        var dock = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(gesture, Dock.Right);
        DockPanel.SetDock(title, Dock.Left);
        DockPanel.SetDock(group, Dock.Left);
        dock.Children.Add(gesture);
        dock.Children.Add(title);
        dock.Children.Add(group);
        return dock;
    }
}
