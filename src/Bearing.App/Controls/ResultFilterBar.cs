using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Bearing.App.ViewModels;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Controls;

/// <summary>
/// A result set's filter bar (#106, design v4 §2), docked between its meta row and its grid. Collapsed until
/// asked for (<c>⌕ Filter</c> or Ctrl/⌘+F in the grid), and never hidden while a filter is active.
/// <para>
/// Left to right: the quick-filter input (autofocused when the bar opens), the column-filter toggle with a
/// count badge, then — only while filtered — the match count, and ✕, which clears every filter and closes.
/// Everything here is bound to <see cref="ResultFilterViewModel"/>; the bar holds no state of its own.
/// </para>
/// </summary>
internal sealed class ResultFilterBar : Border
{
    private readonly ResultFilterViewModel _filter;
    private readonly TextBox _input;
    private readonly ToggleButton _columns;
    private readonly Border _badge;
    private readonly TextBlock _badgeText;
    private readonly TextBlock _match;

    public ResultFilterBar(ResultFilterViewModel filter)
    {
        _filter = filter;
        DataContext = filter;
        Background = Res("Bg.Chrome");
        BorderBrush = Res("Grid.Line");
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(8, 3, 5, 3);
        MinHeight = 28;

        var clear = new Button
        {
            Content = ResultChrome.Icon("Icon.Clear", 10),
            Padding = new Thickness(4, 0),
            MinWidth = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Res("Text.Dim"),
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        ToolTip.SetTip(clear, "Clear the quick filter");
        clear.Click += (_, _) => { _filter.QuickFilter = ""; _input!.Focus(); };

        _input = new TextBox
        {
            PlaceholderText = "Filter rows…",
            FontSize = 11.5,
            MinWidth = 120,
            MaxWidth = 280,
            MinHeight = 0,
            Padding = new Thickness(4, 2),
            CornerRadius = new CornerRadius(6),
            Background = Res("Bg.Window"),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            InnerLeftContent = new Path
            {
                Data = (Geometry?)Application.Current?.FindResource("Icon.Search"),
                Stroke = Res("Text.Dim"),
                StrokeThickness = 1.6,
                Stretch = Stretch.Uniform,
                Width = 11,
                Height = 11,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            },
            InnerRightContent = clear,
        };
        _input.Bind(TextBox.TextProperty, new Binding(nameof(ResultFilterViewModel.QuickFilter)) { Mode = BindingMode.TwoWay });
        _input.KeyDown += OnInputKeyDown;

        _badgeText = new TextBlock { FontSize = 9, FontWeight = FontWeight.ExtraBold, Foreground = Res("Bg.Window") };
        _badge = new Border
        {
            Background = Res("Syntax.Func"),
            CornerRadius = new CornerRadius(6.5),
            MinWidth = 13,
            Height = 13,
            Padding = new Thickness(3, 0),
            Child = _badgeText,
            Margin = new Thickness(3, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var funnel = ResultChrome.Icon("Icon.Filter", 12);
        var funnelContent = new StackPanel { Orientation = Orientation.Horizontal };
        funnelContent.Children.Add(funnel);
        funnelContent.Children.Add(_badge);
        _columns = new ToggleButton
        {
            Content = funnelContent,
            MinWidth = 22,
            Height = 22,
            Padding = new Thickness(5, 0),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_columns, "Filter by column");
        AutomationProperties_SetName(_columns, "Filter by column");
        _columns.Bind(ToggleButton.IsCheckedProperty,
            new Binding(nameof(ResultFilterViewModel.IsColumnFilterRowOpen)) { Mode = BindingMode.TwoWay });
        // The funnel is stroked in the toggle's Foreground.
        _columns.IsCheckedChanged += (_, _) =>
            _columns.Foreground = _columns.IsChecked == true ? Res("Text.Primary") : Res("Text.Muted");
        _columns.Foreground = Res("Text.Muted");

        _match = new TextBlock { FontSize = 11, Foreground = Res("Text.Muted"), VerticalAlignment = VerticalAlignment.Center };

        // Clears every filter and closes: a funnel with a cross, not a bare ✕ — it removes filters, it doesn't
        // only dismiss a panel. Drawn: the ✕ glyph rendered clipped, reading as "›".
        var close = new Button
        {
            Content = ResultChrome.Icon("Icon.FilterClear", 14),
            Width = 24,
            Height = 22,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Res("Text.Dim"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        ToolTip.SetTip(close, "Clear filters and close (Esc)");
        close.Click += (_, _) => _filter.ClearAndClose();

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto") };
        Grid.SetColumn(_input, 0);
        Grid.SetColumn(_columns, 1);
        Grid.SetColumn(_match, 3);
        Grid.SetColumn(close, 4);
        row.Children.Add(_input);
        row.Children.Add(_columns);
        row.Children.Add(_match);
        row.Children.Add(close);
        Child = row;

        _filter.PropertyChanged += OnFilterChanged;
        _filter.FocusRequested += OnFocusRequested;
        DetachedFromVisualTree += (_, _) =>
        {
            _filter.PropertyChanged -= OnFilterChanged;
            _filter.FocusRequested -= OnFocusRequested;
        };
        AttachedToVisualTree += (_, _) =>
        {
            _filter.PropertyChanged -= OnFilterChanged;
            _filter.FocusRequested -= OnFocusRequested;
            _filter.PropertyChanged += OnFilterChanged;
            _filter.FocusRequested += OnFocusRequested;
            Sync();
        };
        Sync();
    }

    /// <summary>The quick-filter input — for the Ctrl+F path and tests.</summary>
    internal TextBox Input => _input;

    private static void AutomationProperties_SetName(Control control, string name)
        => Avalonia.Automation.AutomationProperties.SetName(control, name);

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_filter.Escape()) e.Handled = true;
    }

    private void OnFilterChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    // Posted: the bar may have only just become visible, and an invisible TextBox refuses focus.
    private void OnFocusRequested() => Dispatcher.UIThread.Post(() => _input.Focus(), DispatcherPriority.Loaded);

    private void Sync()
    {
        IsVisible = _filter.IsFilterOpen;
        _input.BorderBrush = _filter.QuickFilter.Length > 0 ? Res("Syntax.Func") : Res("Border");
        if (_input.InnerRightContent is Control clear) clear.IsVisible = _filter.QuickFilter.Length > 0;
        _badge.IsVisible = _filter.HasColumnFilters;
        _badgeText.Text = _filter.ActiveColumnFilterCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _columns.Background = _columns.IsChecked == true ? Res("Border") : Brushes.Transparent;

        _match.IsVisible = _filter.IsFiltered;
        _match.Inlines = new InlineCollection
        {
            new Run(_filter.ShownCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture))
            {
                Foreground = Res("Text.Primary"),
                FontWeight = FontWeight.SemiBold,
            },
            new Run($" / {_filter.LoadedCount:N0} loaded"),
        };
        ToolTip.SetTip(_match, _filter.MatchTip);
    }
}
