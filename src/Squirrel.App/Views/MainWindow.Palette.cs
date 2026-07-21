using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.TextMate;
using Squirrel.App.Completion;
using Squirrel.App.Editing;
using Squirrel.App.Input;
using Squirrel.App.ViewModels;
using Squirrel.Core.Data;
using Squirrel.Sql;
using TextMateSharp.Grammars;

namespace Squirrel.App.Views;

public partial class MainWindow
{
    // ---- command palette (Ctrl+Shift+P) ----
    private Control? _paletteOverlay;
    private TextBox? _paletteSearch;
    private ListBox? _paletteList;

    /// <summary>A Grid sized to the whole window, so an overlay's centered panel actually centers — the
    /// OverlayLayer otherwise arranges children at their desired size, which pins them to the top-left.</summary>
    private Grid FillHost()
    {
        var host = new Grid();
        host[!Layoutable.WidthProperty] = new Binding { Source = this, Path = "Bounds.Width" };
        host[!Layoutable.HeightProperty] = new Binding { Source = this, Path = "Bounds.Height" };
        return host;
    }

    /// <summary>Open the command palette: a fuzzy-searchable list of every applicable command with its
    /// current gesture. Re-invoking while open closes it (toggle). Self-handles its own keys, so global
    /// shortcuts are suppressed while it's up (see <see cref="OnKeyDown"/>).</summary>
    private void ShowPalette()
    {
        if (Vm is null) return;
        if (_paletteOverlay is not null) { HidePalette(); return; }
        if (OverlayLayer.GetOverlayLayer(this) is not { } layer) return;

        var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)) };
        backdrop.PointerPressed += (_, _) => HidePalette();

        _paletteSearch = new TextBox { Watermark = "Type a command…" };
        _paletteSearch.TextChanged += (_, _) => RefreshPaletteList();

        _paletteList = new ListBox
        {
            MaxHeight = 380,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncDataTemplate<PaletteRow>((row, _) => BuildPaletteRow(row), supportsRecycling: true),
        };
        _paletteList.DoubleTapped += (_, _) => RunSelectedPaletteCommand();

        var content = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_paletteSearch, Dock.Top);
        _paletteSearch.Margin = new Thickness(0, 0, 0, 6);
        content.Children.Add(_paletteSearch);
        content.Children.Add(_paletteList);

        var panel = new Border
        {
            Width = 560,
            Padding = new Thickness(10),
            Background = ThemeBrush("Bg.Chrome"),
            BorderBrush = ThemeBrush("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 120, 0, 0),
            Child = content,
        };

        var host = FillHost();
        host.Children.Add(backdrop);
        host.Children.Add(panel);
        host.AddHandler(KeyDownEvent, OnPaletteKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _paletteOverlay = host;
        layer.Children.Add(host);

        RefreshPaletteList();
        _paletteSearch.Focus();
    }

    private void HidePalette()
    {
        if (_paletteOverlay is { } o)
        {
            OverlayLayer.GetOverlayLayer(this)?.Children.Remove(o);
            _paletteOverlay = null;
            _paletteSearch = null;
            _paletteList = null;
        }
    }

    // ---- generic filterable quick-pick (project / connection / database) ----
    private bool AnyOverlayOpen => _paletteOverlay is not null || _quickPickOverlay is not null;
    private Control? _quickPickOverlay;
    private TextBox? _quickPickSearch;
    private ListBox? _quickPickList;
    private System.Collections.Generic.IReadOnlyList<(string Label, Action Pick)> _quickPickItems = System.Array.Empty<(string, Action)>();

    private sealed record QuickPickRow(string Label, Action Pick);

    /// <summary>A single filterable list overlay (type to filter, ↑/↓, Enter). Opening one replaces any
    /// other, so only one picker is ever active.</summary>
    private void ShowQuickPick(string placeholder, System.Collections.Generic.IReadOnlyList<(string Label, Action Pick)> items)
    {
        if (Vm is null || items.Count == 0) return;
        HidePalette();
        HideQuickPick();
        if (OverlayLayer.GetOverlayLayer(this) is not { } layer) return;
        _quickPickItems = items;

        var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)) };
        backdrop.PointerPressed += (_, _) => HideQuickPick();

        _quickPickSearch = new TextBox { Watermark = placeholder };
        _quickPickSearch.TextChanged += (_, _) => RefreshQuickPick();
        _quickPickList = new ListBox
        {
            MaxHeight = 380,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncDataTemplate<QuickPickRow>((row, _) =>
                new TextBlock { Text = row.Label, Margin = new Thickness(4, 2), Foreground = ThemeBrush("Text.Primary") }, supportsRecycling: true),
        };
        _quickPickList.DoubleTapped += (_, _) => RunSelectedQuickPick();

        var content = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_quickPickSearch, Dock.Top);
        _quickPickSearch.Margin = new Thickness(0, 0, 0, 6);
        content.Children.Add(_quickPickSearch);
        content.Children.Add(_quickPickList);

        var panel = new Border
        {
            Width = 460,
            Padding = new Thickness(10),
            Background = ThemeBrush("Bg.Chrome"),
            BorderBrush = ThemeBrush("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 120, 0, 0),
            Child = content,
        };

        var host = FillHost();
        host.Children.Add(backdrop);
        host.Children.Add(panel);
        host.AddHandler(KeyDownEvent, OnQuickPickKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _quickPickOverlay = host;
        layer.Children.Add(host);

        RefreshQuickPick();
        _quickPickSearch.Focus();
    }

    private void HideQuickPick()
    {
        if (_quickPickOverlay is { } o)
        {
            OverlayLayer.GetOverlayLayer(this)?.Children.Remove(o);
            _quickPickOverlay = null;
            _quickPickSearch = null;
            _quickPickList = null;
        }
    }

    private void RefreshQuickPick()
    {
        if (_quickPickList is null) return;
        var query = _quickPickSearch?.Text ?? "";
        System.Collections.Generic.IEnumerable<(string Label, Action Pick)> filtered = string.IsNullOrWhiteSpace(query)
            ? _quickPickItems
            : _quickPickItems
                .Select(x => (x, score: PaletteFilter.Score(x.Label, query.Trim())))
                .Where(t => t.score.HasValue)
                .OrderByDescending(t => t.score!.Value)
                .Select(t => t.x);
        _quickPickList.ItemsSource = filtered.Select(x => new QuickPickRow(x.Label, x.Pick)).ToList();
        if (_quickPickList.ItemCount > 0) _quickPickList.SelectedIndex = 0;
    }

    private void MoveQuickPickSelection(int dir)
    {
        if (_quickPickList is null || _quickPickList.ItemCount == 0) return;
        var n = _quickPickList.ItemCount;
        _quickPickList.SelectedIndex = (_quickPickList.SelectedIndex + dir + n) % n;
        _quickPickList.ScrollIntoView(_quickPickList.SelectedIndex);
    }

    private void RunSelectedQuickPick()
    {
        if (_quickPickList?.SelectedItem is not QuickPickRow row) return;
        HideQuickPick();
        row.Pick();
    }

    private void OnQuickPickKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: HideQuickPick(); e.Handled = true; break;
            case Key.Enter: RunSelectedQuickPick(); e.Handled = true; break;
            case Key.Down: MoveQuickPickSelection(+1); e.Handled = true; break;
            case Key.Up: MoveQuickPickSelection(-1); e.Handled = true; break;
        }
    }

    private void RefreshPaletteList()
    {
        if (_paletteList is null) return;
        var query = _paletteSearch?.Text ?? "";
        var rows = PaletteFilter.Rank(_commands.All.Where(c => c.CanRun()), query)
            .Select(c => new PaletteRow(c, _dispatcher.Keymap.DisplayGesture(c.Id)))
            .ToList();
        _paletteList.ItemsSource = rows;
        if (rows.Count > 0) _paletteList.SelectedIndex = 0;
    }

    private void MovePaletteSelection(int dir)
    {
        if (_paletteList is null || _paletteList.ItemCount == 0) return;
        var n = _paletteList.ItemCount;
        _paletteList.SelectedIndex = (_paletteList.SelectedIndex + dir + n) % n;
        _paletteList.ScrollIntoView(_paletteList.SelectedIndex);
    }

    private void RunSelectedPaletteCommand()
    {
        if (_paletteList?.SelectedItem is not PaletteRow row) return;
        HidePalette();
        if (row.Command.CanRun()) CrashReporter.Observe(row.Command.Run(), $"command '{row.Command.Id}'");
    }

    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: HidePalette(); e.Handled = true; break;
            case Key.Enter: RunSelectedPaletteCommand(); e.Handled = true; break;
            case Key.Down: MovePaletteSelection(+1); e.Handled = true; break;
            case Key.Up: MovePaletteSelection(-1); e.Handled = true; break;
        }
    }

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
