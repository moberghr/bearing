using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Bearing.App.Controls;
using Bearing.App.Formatting;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// What an inline result block shows (spec §5): the error line, a no-row message, the meta line, the caps, NULLs
/// and digit grouping — read off the realized text, not the look (§4.3).
/// </summary>
[Collection(UiTestCollection.Name)]
public class InlineResultViewTests
{
    private readonly UiTestSession _ui;

    public InlineResultViewTests(UiTestSession ui) => _ui = ui;

    private static ResultSetViewModel Rows(int count, Type? clr = null, object? value = null) => new(
        new QueryResult([new ColumnDescriptor("n", "int8", clr ?? typeof(long))],
            Enumerable.Range(0, count).Select(i => new object?[] { value ?? (object)(long)i }).ToList(), count,
            TimeSpan.FromMilliseconds(12), null, null, false),
        "select n;", pageable: false);

    private static string[] Texts(ResultSetViewModel[] sets, string? caption = null)
    {
        var view = new InlineResultView(new InlineResultZone(0, sets, caption), () => { }, () => { });
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();
        window.UpdateLayout();
        return view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
    }

    [Fact]
    public Task A_failed_statement_shows_its_error() => _ui.Run(() =>
    {
        var failed = new ResultSetViewModel(
            new QueryResult([], [], 0, TimeSpan.Zero, null, new QueryError("relation \"nope\" does not exist", "42P01", null), false),
            "select * from nope;", pageable: false);

        var texts = Texts([failed]);

        Assert.Contains("Error: relation \"nope\" does not exist", texts);
        Assert.Contains(texts, t => t.StartsWith("error · ", StringComparison.Ordinal));
    });

    [Fact]
    public Task A_statement_without_rows_shows_its_message() => _ui.Run(() =>
    {
        var update = new ResultSetViewModel(
            new QueryResult([], [], 5, TimeSpan.Zero, "UPDATE 5", null, false), "update t set x = 1;", pageable: false);

        Assert.Contains("UPDATE 5", Texts([update]));
    });

    [Theory]
    [InlineData(1, "1 row · 12 ms")]
    [InlineData(3, "3 rows · 12 ms")]
    [InlineData(250, "250 rows · 200 shown · 12 ms")]
    public void The_meta_line_counts_rows(int count, string expected) => _ui.Run(() =>
    {
        Assert.Contains(expected, Texts([Rows(count)]));
    }).GetAwaiter().GetResult();

    [Fact]
    public Task At_most_the_cap_is_built_and_ten_rows_show_before_it_scrolls() => _ui.Run(() =>
    {
        var view = new InlineResultView(new InlineResultZone(0, [Rows(250)], null), () => { }, () => { });
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();
        window.UpdateLayout();

        var cells = view.GetLogicalDescendants().OfType<TextBlock>().Where(t => Grid.GetRow(t) >= 0 && t.Parent is Grid g && g.RowDefinitions.Count > 0);
        Assert.Equal(InlineResultView.MaxRenderedRows, cells.Count());
        var scroll = view.GetLogicalDescendants().OfType<ScrollViewer>().Single(s => !double.IsInfinity(s.MaxHeight));
        Assert.Equal(ResultGridChrome.RowMinHeight * InlineResultView.VisibleRows, scroll.MaxHeight);
    });

    [Fact]
    public Task Several_sets_carry_the_caption_and_a_line_each() => _ui.Run(() =>
    {
        var texts = Texts([Rows(1), Rows(2)], "2 result sets from this batch");

        Assert.Contains("2 result sets from this batch", texts);
        Assert.Contains("Result 1 · 1 row · 12 ms", texts);
        Assert.Contains("Result 2 · 2 rows · 12 ms", texts);
    });

    [Fact]
    public Task Nulls_read_as_null() => _ui.Run(() =>
    {
        var set = new ResultSetViewModel(
            new QueryResult([new ColumnDescriptor("s", "text", typeof(string))], [[null]], 1, TimeSpan.Zero, null, null, false),
            "select null;", pageable: false);

        Assert.Contains("NULL", Texts([set]));
    });

    /// <summary>Through the grid's own display step (§9.10c): grouped where the grid groups, and not when the
    /// setting is off or the column is text.</summary>
    [Fact]
    public Task Numbers_group_like_the_grid() => _ui.Run(() =>
    {
        var was = NumberGrouping.Enabled;
        try
        {
            NumberGrouping.Enabled = true;
            Assert.Contains("1,234,567", Texts([Rows(1, value: 1234567L)]));
            Assert.Contains("1234567", Texts([Rows(1, typeof(string), "1234567")]));

            NumberGrouping.Enabled = false;
            Assert.Contains("1234567", Texts([Rows(1, value: 1234567L)]));
        }
        finally
        {
            NumberGrouping.Enabled = was;
        }
    });
}
