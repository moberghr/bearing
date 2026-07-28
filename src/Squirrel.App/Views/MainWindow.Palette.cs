using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Squirrel.App.Controls;
using Squirrel.App.Input;

namespace Squirrel.App.Views;

public partial class MainWindow
{
    // ---- command palette (Ctrl+Shift+P) + generic quick-pick (project / connection / database) ----
    // Both are instances of the shared Controls/FilterableListOverlay<T>; this file only supplies each
    // one's rows, row template, and pick action.
    private FilterableListOverlay<PaletteRow>? _palette;
    private FilterableListOverlay<QuickPickRow>? _quickPick;

    private bool PaletteOpen => _palette?.IsOpen == true;
    private bool QuickPickOpen => _quickPick?.IsOpen == true;
    private bool AnyOverlayOpen => PaletteOpen || QuickPickOpen;

    private void HidePalette() => _palette?.Hide();
    private void HideQuickPick() => _quickPick?.Hide();

    /// <summary>Open the command palette: a fuzzy-searchable list of every applicable command with its
    /// current gesture. Re-invoking while open closes it (toggle). Self-handles its own keys, so global
    /// shortcuts are suppressed while it's up (see <see cref="OnKeyDown"/>).</summary>
    private void ShowPalette()
    {
        if (Vm is null) return;
        if (PaletteOpen) { _palette!.Hide(); return; }
        _palette = new FilterableListOverlay<PaletteRow>(
            this, "Type a command…", 560,
            new FuncDataTemplate<PaletteRow>((row, _) => BuildPaletteRow(row), supportsRecycling: true),
            query: PaletteRows,
            onPick: row => { if (row.Command.CanRun()) CrashReporter.Observe(row.Command.Run(), $"command '{row.Command.Id}'"); });
        _palette.Show();
    }

    /// <summary>Rank every runnable command against the query, pairing each with its current gesture text.</summary>
    private IReadOnlyList<PaletteRow> PaletteRows(string query)
        => PaletteFilter.Rank(_commands.All.Where(c => c.CanRun()), query)
            .Select(c => new PaletteRow(c, _dispatcher.Keymap.DisplayGesture(c.Id)))
            .ToList();

    /// <summary>Open a single filterable quick-pick (project / connection / database). Opening one replaces
    /// any other overlay, so only one picker is ever active.</summary>
    private void ShowQuickPick(string placeholder, IReadOnlyList<(string Label, Action Pick)> items)
    {
        if (Vm is null || items.Count == 0) return;
        _palette?.Hide();
        _quickPick?.Hide();
        _quickPick = new FilterableListOverlay<QuickPickRow>(
            this, placeholder, 460,
            new FuncDataTemplate<QuickPickRow>((row, _) =>
                new TextBlock { Text = row.Label, Margin = new Thickness(4, 2), Foreground = ThemeBrush("Text.Primary") }, supportsRecycling: true),
            query: q => QuickPickRows(items, q),
            onPick: row => row.Pick());
        _quickPick.Show();
    }

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

    private Control BuildPaletteRow(PaletteRow row)
    {
        var title = new TextBlock { Text = row.Command.Title, VerticalAlignment = VerticalAlignment.Center };
        var group = new TextBlock
        {
            Text = row.Command.Group,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("Text.Faint"),
            FontSize = 11,
        };
        var gesture = new TextBlock
        {
            Text = row.Gesture ?? "",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("Text.Dim"),
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
