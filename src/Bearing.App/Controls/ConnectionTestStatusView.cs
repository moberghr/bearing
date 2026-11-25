using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Connections;

namespace Bearing.App.Controls;

/// <summary>
/// The connection dialog's Test status row: the beacon, a coloured headline, a dim detail line and a
/// right-aligned figure. It draws a <see cref="ConnectionTestStatus"/> and decides nothing itself.
/// <para>
/// State colour is on the beacon's stroke and the headline text only, never a fill (§9.3a). A phase with no
/// session state to show (not tested, stopped) hides the beacon rather than drawing a new glyph for it; its
/// column keeps its width so the text does not move when a test starts.
/// </para>
/// <para>
/// It sits in a bordered panel on a neutral surface, and every state but a failure is the same two lines, so
/// the panel keeps one height from "Not tested" through "Connected" and the dialog around it does not
/// re-lay out as a test runs. Only an error grows it, up to the detail's cap.
/// </para>
/// </summary>
public sealed class ConnectionTestStatusView : Border
{
    /// <summary>The cap on a failure's message: about four lines, after which it scrolls, so a
    /// paragraph-length driver error cannot push the dialog's buttons off screen.</summary>
    private const double DetailMaxHeight = 76;

    private readonly ConnectionBeacon _beacon = new()
    {
        Width = 16,
        Height = 16,
        VerticalAlignment = VerticalAlignment.Top,
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 1, 0, 0),
    };

    private readonly TextBlock _headline = new() { Name = "TestHeadline", FontWeight = FontWeight.SemiBold };

    // Selectable so an error can be copied in part; wraps because the useful half of a driver message
    // (SQLSTATE, host, TLS/auth detail) is usually past the dialog's right edge.
    private readonly SelectableTextBlock _detail = new() { Name = "TestDetail", TextWrapping = TextWrapping.Wrap };

    private readonly TextBlock _aside = new()
    {
        Name = "TestAside",
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(8, 1, 0, 0),
    };

    private readonly Button _copy = new()
    {
        Name = "TestCopy",
        Content = "Copy",
        Padding = new Thickness(8, 2),
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(8, 0, 0, 0),
    };

    private readonly ScrollViewer _detailScroll;

    public ConnectionTestStatusView()
    {
        Background = Tokens.Res("Bg.Chrome");
        BorderBrush = Tokens.Res("Border");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(5);
        Padding = new Thickness(12, 9);

        var small = Tokens.Metric("Font.Small");
        _detail.FontSize = small;
        _aside.FontSize = small;
        _copy.FontSize = small;
        _aside.Foreground = Tokens.Res("Text.Faint");

        // Horizontal scrolling must stay Disabled or the text is measured unbounded and never wraps.
        _detailScroll = new ScrollViewer
        {
            MaxHeight = DetailMaxHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _detail,
        };

        var text = new StackPanel { Spacing = 2, Children = { _headline, _detailScroll } };
        var side = new StackPanel { Orientation = Orientation.Horizontal, Children = { _aside, _copy } };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(side, 2);
        Child = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("26,*,Auto"),
            Children = { _beacon, text, side },
        };

        _copy.Click += OnCopyClick;
        Show(ConnectionTestStatus.NotTested);
    }

    /// <summary>What the row currently shows.</summary>
    public ConnectionTestStatus Status { get; private set; } = ConnectionTestStatus.NotTested;

    public void Show(ConnectionTestStatus status)
    {
        Status = status;
        var failed = status.Phase == ConnectionTestPhase.Failed;

        _beacon.IsVisible = status.Beacon is not null;
        if (status.Beacon is { } state) _beacon.State = state;

        _headline.Text = status.Headline;
        _headline.Foreground = Tokens.Res(status.Phase switch
        {
            ConnectionTestPhase.Testing => "Status.Connecting",
            ConnectionTestPhase.Succeeded => "Status.Connected",
            ConnectionTestPhase.Failed => "Status.Disconnected",
            _ => "Text.Faint",
        });
        // Only a settled result is weighted; "Not tested" is a caption, not an announcement.
        _headline.FontWeight = status.Beacon is null ? FontWeight.Normal : FontWeight.SemiBold;

        _detail.Text = status.Detail;
        _detail.Foreground = Tokens.Res(failed ? "Text.Muted" : "Text.Dim");
        _detailScroll.IsVisible = status.Detail.Length > 0;

        _aside.Text = status.Aside;
        _aside.IsVisible = status.Aside.Length > 0;
        _copy.IsVisible = failed;
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        // Best-effort: a clipboard that refuses must not take the dialog down (async void).
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(Status.Detail);
        }
        catch (Exception)
        {
            // The message is still selectable in place.
        }
    }
}
