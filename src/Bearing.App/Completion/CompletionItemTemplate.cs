using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;   // not System.IO.Path (ImplicitUsings)
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Completion;

/// <summary>
/// The completion row: kind glyph, name, dimmed detail, right-aligned trailing preview. Replaces the
/// stock AvaloniaEdit row, which rendered <c>ICompletionData.Content</c> as a plain string — so the
/// two-column look was faked by padding the label with four spaces, and
/// <see cref="Bearing.Core.Completion.Suggestion.TrailingText"/> (the join-predicate preview) was
/// never shown at all.
/// <para>Built in code, like the app's other list rows, because the data type is internal and the
/// glyph is a token-coloured <see cref="Geometry"/> rather than an image.</para>
/// </summary>
internal static class CompletionItemTemplate
{
    /// <summary>
    /// One shared instance — the popup is rebuilt per keystroke and the template itself is stateless.
    /// Recycling is off on purpose: the row reads its item once at build time (no bindings), so a reused
    /// container would keep the previous row's glyph and text after the list is narrowed.
    /// </summary>
    public static readonly IDataTemplate Instance =
        new FuncDataTemplate<BearingCompletionData>((item, _) => Row(item), supportsRecycling: false);

    /// <summary>
    /// Builds one row. The item is nullable on purpose: when the list is re-sourced, Avalonia clears each
    /// recycled container by pushing a null content through the same template, so dereferencing here took
    /// the app down with a <see cref="System.NullReferenceException"/> on the next keystroke.
    /// </summary>
    private static Control Row(BearingCompletionData? item)
    {
        if (item is null) return new Control();

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*"),
            Height = 22,
        };

        var icon = new Path
        {
            Data = Application.Current?.FindResource(item.IconKey) as Geometry,
            Stroke = Res(item.IconColorKey),
            StrokeThickness = 1.4,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Width = 13,
            Height = 13,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);

        var name = new TextBlock
        {
            Text = item.DisplayText,
            Foreground = Res("Text.Primary"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);

        grid.Children.Add(icon);
        grid.Children.Add(name);

        if (item.DetailText is { Length: > 0 } detail)
        {
            var detailText = new TextBlock
            {
                Text = detail,
                Foreground = Res("Text.Dim"),
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(detailText, 2);
            grid.Children.Add(detailText);
        }

        if (item.TrailingText is { Length: > 0 } trailing)
        {
            // The FK predicate a join snippet will insert — worth seeing before committing to it.
            var trailingText = new TextBlock
            {
                Text = trailing,
                Foreground = Res("Text.Faint"),
                FontSize = 11,
                Margin = new Thickness(16, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(trailingText, 3);
            grid.Children.Add(trailingText);
        }

        return grid;
    }
}
