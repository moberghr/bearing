using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bearing.App.Controls;

/// <summary>A centered, single-select filterable list overlay: a dimmed backdrop, a search box, and a
/// ranked list. Type to filter, ↑/↓ to move, Enter or double-click to pick, Esc or click-outside to close.
/// Traps Tab within itself so focus can't walk to the chrome behind the backdrop. Shared by the command
/// palette and the project/connection/database quick-picks (previously two near-identical hand-built
/// overlays in <c>MainWindow</c>).</summary>
public sealed class FilterableListOverlay<T>
{
    private readonly Visual _owner;
    private readonly string _placeholder;
    private readonly double _width;
    private readonly IDataTemplate _itemTemplate;
    private readonly Func<string, IReadOnlyList<T>> _query;
    private readonly Action<T> _onPick;

    private Control? _host;
    private TextBox? _search;
    private ListBox? _list;

    /// <param name="owner">The visual whose <see cref="OverlayLayer"/> hosts the overlay (the window).</param>
    /// <param name="placeholder">Search box watermark.</param>
    /// <param name="width">Fixed width of the centered panel.</param>
    /// <param name="itemTemplate">Row template for the list.</param>
    /// <param name="query">Produces the ranked rows for the current search text (re-run on each keystroke).</param>
    /// <param name="onPick">Invoked with the selected row after the overlay closes.</param>
    public FilterableListOverlay(Visual owner, string placeholder, double width,
        IDataTemplate itemTemplate, Func<string, IReadOnlyList<T>> query, Action<T> onPick)
    {
        _owner = owner;
        _placeholder = placeholder;
        _width = width;
        _itemTemplate = itemTemplate;
        _query = query;
        _onPick = onPick;
    }

    public bool IsOpen => _host is not null;

    public void Show()
    {
        if (_host is not null) return;
        if (OverlayLayer.GetOverlayLayer(_owner) is not { } layer) return;

        var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)) };
        backdrop.PointerPressed += (_, _) => Hide();

        _search = new TextBox { PlaceholderText = _placeholder };
        _search.TextChanged += (_, _) => Refresh();

        _list = new ListBox
        {
            MaxHeight = 380,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = _itemTemplate,
        };
        _list.DoubleTapped += (_, _) => RunSelected();

        var content = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_search, Dock.Top);
        _search.Margin = new Thickness(0, 0, 0, 6);
        content.Children.Add(_search);
        content.Children.Add(_list);

        var panel = new Border
        {
            Width = _width,
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

        // A Grid sized to the whole window so the centered panel actually centers — the OverlayLayer
        // otherwise arranges children at desired size, pinning them top-left. Trap Tab/Shift+Tab so focus
        // cycles among the overlay's own controls (search ↔ list) and can't walk out to the chrome behind
        // the backdrop.
        var host = new Grid();
        host[!Layoutable.WidthProperty] = new Binding { Source = _owner, Path = "Bounds.Width" };
        host[!Layoutable.HeightProperty] = new Binding { Source = _owner, Path = "Bounds.Height" };
        KeyboardNavigation.SetTabNavigation(host, KeyboardNavigationMode.Cycle);
        host.Children.Add(backdrop);
        host.Children.Add(panel);
        host.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _host = host;
        layer.Children.Add(host);

        Refresh();
        _search.Focus();
    }

    public void Hide()
    {
        if (_host is { } h)
        {
            OverlayLayer.GetOverlayLayer(_owner)?.Children.Remove(h);
            _host = null;
            _search = null;
            _list = null;
        }
    }

    private void Refresh()
    {
        if (_list is null) return;
        _list.ItemsSource = _query(_search?.Text ?? "");
        if (_list.ItemCount > 0) _list.SelectedIndex = 0;
    }

    private void Move(int dir)
    {
        if (_list is null || _list.ItemCount == 0) return;
        var n = _list.ItemCount;
        _list.SelectedIndex = (_list.SelectedIndex + dir + n) % n;
        _list.ScrollIntoView(_list.SelectedIndex);
    }

    private void RunSelected()
    {
        if (_list?.SelectedItem is not T item) return;
        Hide();
        _onPick(item);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Hide(); e.Handled = true; break;
            case Key.Enter: RunSelected(); e.Handled = true; break;
            case Key.Down: Move(+1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
        }
    }

    /// <summary>Resolve a token brush from app resources (falls back to transparent if missing).</summary>
    private static IBrush ThemeBrush(string key)
        => (Application.Current?.FindResource(key) as IBrush) ?? Brushes.Transparent;
}
