using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Services;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Controls;

/// <summary>
/// The line-numbered, kind-coloured list of statements a write confirmation is about to run: a header with
/// the count and a copy-all button, then one row per statement (line number · kind tag · SQL). Extracted
/// from the retired floating pending-changes panel, whose colour coding (design RESULTS_GRID §5) this keeps —
/// it now lives inside the confirmation dialog instead of a separate manual preview step.
/// </summary>
public static class SqlStatementList
{
    /// <summary>Statements rendered before the list gives up and says how many more there are. A batch can be
    /// a whole migration file; the dialog must stay openable (and honest about what it left out).</summary>
    public const int MaxRendered = 100;

    /// <summary>Build the list for <paramref name="request"/>. Scrolls internally; the caller bounds its height.</summary>
    public static Control Build(WriteConfirmation request)
    {
        var statements = request.Statements;

        var count = new TextBlock
        {
            Text = statements.Count == 1 ? "1 statement" : $"{statements.Count} statements",
            Foreground = Res("Text.Dim"),
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var copy = new Button
        {
            Content = "⧉ Copy",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Res("Text.Dim"),
            Padding = new Thickness(6, 2),
        };
        copy.Click += (s, _) =>
        {
            if (s is Visual v) TopLevel.GetTopLevel(v)?.Clipboard?.SetTextAsync(request.Script);
        };

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(copy, 1);
        headerGrid.Children.Add(count);
        headerGrid.Children.Add(copy);
        var header = new Border
        {
            Padding = new Thickness(12, 6),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Res("Border"),
            Child = headerGrid,
        };
        DockPanel.SetDock(header, Dock.Top);

        var list = new StackPanel { Spacing = 3 };
        foreach (var (statement, index) in statements.Take(MaxRendered).Select((s, i) => (s, i)))
            list.Children.Add(Row(index + 1, statement));
        if (statements.Count > MaxRendered)
            list.Children.Add(new TextBlock
            {
                Text = $"… and {statements.Count - MaxRendered} more (copy to see them all)",
                Foreground = Res("Text.Dim"),
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
            });

        var body = new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(12, 8), Child = list },
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(header);
        dock.Children.Add(body);

        return new Border
        {
            Background = Res("Bg.Editor"),
            BorderBrush = Res("Border.Control"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = dock,
        };
    }

    /// <summary>`  3  DELETE  delete from public.orders where id = 9;` — the tag carries the colour, and a
    /// statement that only reads is dimmed so the writes stand out in a mixed batch.</summary>
    private static Control Row(int number, WriteStatement statement)
    {
        var num = new TextBlock
        {
            Text = $"{number,3}",
            Foreground = Res("Text.Faint"),
            FontFamily = MonoFont,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var kind = new TextBlock
        {
            Text = statement.Kind,
            Foreground = KindBrush(statement.Kind),
            FontFamily = MonoFont,
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Top,
            Width = 86,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 8, 0),
        };
        var sql = new TextBlock
        {
            Text = statement.Sql,
            Foreground = statement.IsRisky ? Res("Text.Code") : Res("Text.Faint"),
            FontFamily = MonoFont,
            TextWrapping = TextWrapping.Wrap,
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        Grid.SetColumn(kind, 1);
        Grid.SetColumn(sql, 2);
        row.Children.Add(num);
        row.Children.Add(kind);
        row.Children.Add(sql);
        return row;
    }

    private static readonly FontFamily MonoFont =
        new("Iosevka Nerd Font Mono,Cascadia Code,Consolas,Menlo,monospace");

    /// <summary>Colour by what the statement does: adds green, changes amber, removals red, reads dim.
    /// A multi-verb tag ("DROP + CREATE") takes the colour of its first, most alarming verb.</summary>
    public static IBrush KindBrush(string kind) => kind.Split(" + ")[0] switch
    {
        "INSERT" or "CREATE" or "SELECT INTO" => Res("Ok.Green"),
        "UPDATE" or "ALTER" or "MERGE" or "COPY" or "REFRESH" or "GRANT" or "REVOKE" or "CALL" or "DO"
            => Res("Warn.Amber"),
        "DELETE" or "DROP" or "TRUNCATE" => Res("Error.Red"),
        _ => Res("Text.Dim"),
    };
}
