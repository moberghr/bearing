using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Squirrel.App.ViewModels;

namespace Squirrel.App.Controls;

/// <summary>The floating, color-coded panel of the write statements a save would run (design
/// RESULTS_GRID §5): a dim backdrop with a bottom-right card that lists the INSERT/UPDATE/DELETE statements
/// and offers Copy / Discard / Run &amp; save. Click-outside closes it; the shell routes Escape to
/// <see cref="Hide"/>. Extracted from <c>MainWindow</c>, which keeps only a thin bridge to the result set's
/// preview/discard/save.</summary>
public sealed class PendingChangesOverlay
{
    private readonly Visual _owner;
    private Control? _host;

    public PendingChangesOverlay(Visual owner) => _owner = owner;

    public bool IsOpen => _host is not null;

    /// <summary>Show the panel over a dim backdrop for the given statements (empty → no-op). Any previously
    /// open panel is closed first. <paramref name="onDiscard"/>/<paramref name="onSave"/> run after the
    /// panel closes — the shell wires them to the result set's discard/save plus a re-render.</summary>
    public void Show(IReadOnlyList<PendingStatement> statements, Func<Task> onDiscard, Func<Task> onSave)
    {
        Hide();
        if (statements.Count == 0) return;
        if (OverlayLayer.GetOverlayLayer(_owner) is not { } layer) return;

        var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)) };
        backdrop.PointerPressed += (_, _) => Hide(); // click outside closes

        var panel = BuildPanel(statements, onDiscard, onSave);
        panel.HorizontalAlignment = HorizontalAlignment.Right;
        panel.VerticalAlignment = VerticalAlignment.Bottom;
        panel.Margin = new Thickness(0, 0, 20, 20);

        var host = new Grid();
        // Trap Tab within the overlay so focus can't walk out to the chrome behind the backdrop; the panel
        // is made focusable and focused on open so the trap has somewhere to start.
        KeyboardNavigation.SetTabNavigation(host, KeyboardNavigationMode.Cycle);
        panel.Focusable = true;
        host.Children.Add(backdrop);
        host.Children.Add(panel);
        _host = host;
        layer.Children.Add(host);
        panel.Focus();
    }

    public void Hide()
    {
        if (_host is { } h)
        {
            OverlayLayer.GetOverlayLayer(_owner)?.Children.Remove(h);
            _host = null;
        }
    }

    private Control BuildPanel(IReadOnlyList<PendingStatement> statements, Func<Task> onDiscard, Func<Task> onSave)
    {
        // Header: "N statements" + copy.
        var count = new TextBlock
        {
            Text = statements.Count == 1 ? "1 statement" : $"{statements.Count} statements",
            Foreground = ThemeBrush("Text.Dim"), FontSize = 11, FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var copy = new Button { Content = "⧉ Copy", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = ThemeBrush("Text.Dim"), Padding = new Thickness(6, 2) };
        copy.Click += (_, _) => TopLevel.GetTopLevel(_owner)?.Clipboard?.SetTextAsync(string.Join("\n", statements.Select(s => s.Sql)));
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(copy, 1);
        headerGrid.Children.Add(count);
        headerGrid.Children.Add(copy);
        var header = new Border { Padding = new Thickness(12, 8), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = ThemeBrush("Border"), Child = headerGrid };
        DockPanel.SetDock(header, Dock.Top);

        // Body: line-numbered, kind-colored statements.
        var list = new StackPanel { Spacing = 2 };
        for (var i = 0; i < statements.Count; i++)
        {
            var num = new TextBlock { Text = $"{i + 1,3} ", Foreground = ThemeBrush("Text.Faint"), FontFamily = MonoFont, VerticalAlignment = VerticalAlignment.Top };
            var sql = new TextBlock { Text = statements[i].Sql, Foreground = KindBrush(statements[i].Kind), FontFamily = MonoFont, TextWrapping = TextWrapping.Wrap };
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal };
            rowPanel.Children.Add(num);
            rowPanel.Children.Add(sql);
            list.Children.Add(rowPanel);
        }
        var body = new ScrollViewer { Content = new Border { Padding = new Thickness(12, 8), Child = list } };

        // Footer: Discard + Run & save.
        var discard = new Button { Content = "Discard", Margin = new Thickness(0, 0, 8, 0), Background = Brushes.Transparent, BorderBrush = ThemeBrush("Error.Red"), BorderThickness = new Thickness(1), Foreground = ThemeBrush("Error.Red") };
        discard.Click += async (_, _) => { Hide(); await onDiscard(); };
        var run = new Button { Content = "✓ Run & save", Background = ThemeBrush("Ok.Green"), Foreground = ThemeBrush("Bg.Editor") };
        run.Click += async (_, _) => { Hide(); await onSave(); };
        var footerButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footerButtons.Children.Add(discard);
        footerButtons.Children.Add(run);
        var footer = new Border { Padding = new Thickness(12, 8), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = ThemeBrush("Border"), Child = footerButtons };
        DockPanel.SetDock(footer, Dock.Bottom);

        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(body);

        return new Border
        {
            Width = 520,
            MaxHeight = 420,
            Background = ThemeBrush("Bg.Chrome"),
            BorderBrush = ThemeBrush("Border.Control"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 20, Blur = 50, Spread = -12, Color = Color.FromArgb(0xBF, 0, 0, 0) }),
            Child = dock,
        };
    }

    private static readonly FontFamily MonoFont = new("Iosevka Nerd Font Mono,Cascadia Code,Consolas,Menlo,monospace");

    /// <summary>Statement color by kind: INSERT green, UPDATE amber, DELETE red (design §5).</summary>
    private static IBrush KindBrush(string kind) => kind switch
    {
        "INSERT" => ThemeBrush("Ok.Green"),
        "UPDATE" => new SolidColorBrush(Color.FromRgb(0xE6, 0xC3, 0x84)),
        "DELETE" => ThemeBrush("Error.Red"),
        _ => ThemeBrush("Text.Primary"),
    };

    private static IBrush ThemeBrush(string key)
        => (Application.Current?.FindResource(key) as IBrush) ?? Brushes.Transparent;
}
