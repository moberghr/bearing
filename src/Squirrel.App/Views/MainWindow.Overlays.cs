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
    // ---- Floating pending-changes script panel (design RESULTS_GRID §5) ----------------------
    private Control? _pendingScriptOverlay;

    /// <summary>Open a floating, color-coded panel of the write statements a save would run, over a dim
    /// backdrop (bottom-right). Copy / Discard / Run &amp; save act on the result set's pending changes.</summary>
    private void ShowPendingScript(ResultSetViewModel rs)
    {
        if (Vm is null) return;
        HidePendingScript();
        var statements = Vm.Execution.PreviewChangeStatements(rs);
        if (statements.Count == 0) return;
        if (OverlayLayer.GetOverlayLayer(this) is not { } layer) return;

        var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)) };
        backdrop.PointerPressed += (_, _) => HidePendingScript(); // click outside closes

        var panel = BuildPendingScriptPanel(rs, statements);
        panel.HorizontalAlignment = HorizontalAlignment.Right;
        panel.VerticalAlignment = VerticalAlignment.Bottom;
        panel.Margin = new Thickness(0, 0, 20, 20);

        var host = new Grid();
        host.Children.Add(backdrop);
        host.Children.Add(panel);
        _pendingScriptOverlay = host;
        layer.Children.Add(host);
    }

    private void HidePendingScript()
    {
        if (_pendingScriptOverlay is { } o)
        {
            OverlayLayer.GetOverlayLayer(this)?.Children.Remove(o);
            _pendingScriptOverlay = null;
        }
    }

    private Control BuildPendingScriptPanel(ViewModels.ResultSetViewModel rs, System.Collections.Generic.IReadOnlyList<ViewModels.PendingStatement> statements)
    {
        // Header: "N statements" + copy.
        var count = new TextBlock
        {
            Text = statements.Count == 1 ? "1 statement" : $"{statements.Count} statements",
            Foreground = ThemeBrush("Text.Dim"), FontSize = 11, FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var copy = new Button { Content = "⧉ Copy", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = ThemeBrush("Text.Dim"), Padding = new Thickness(6, 2) };
        copy.Click += (_, _) => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(string.Join("\n", statements.Select(s => s.Sql)));
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
        discard.Click += async (_, _) => { HidePendingScript(); if (Vm is not null) { await Vm.Execution.DiscardChangesAsync(rs); RebuildResults(Vm.Workspace.SelectedTab); } };
        var run = new Button { Content = "✓ Run & save", Background = ThemeBrush("Ok.Green"), Foreground = ThemeBrush("Bg.Editor") };
        run.Click += async (_, _) => { HidePendingScript(); if (Vm is not null) { await Vm.Execution.SaveChangesAsync(rs); ResultsView.RefreshRowHighlights(); } };
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
    private IBrush KindBrush(string kind) => kind switch
    {
        "INSERT" => ThemeBrush("Ok.Green"),
        "UPDATE" => new SolidColorBrush(Color.FromRgb(0xE6, 0xC3, 0x84)),
        "DELETE" => ThemeBrush("Error.Red"),
        _ => ThemeBrush("Text.Primary"),
    };
}
