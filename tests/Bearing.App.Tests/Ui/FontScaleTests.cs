using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Bearing.App.Controls;
using Bearing.App.Theming;
using Bearing.Core.Workspace;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The type scale as a settable token (#52). Font sizes were roughly thirty literals across seven files,
/// which is why they could not be made settable: a setting plumbed into a dozen call sites is one the next
/// control added to a panel quietly ignores.
/// <para>
/// A UI collection test because the tokens live in the application's resources and the grid's sizes are only
/// real once a control has been built from them.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class FontScaleTests
{
    private readonly UiTestSession _ui;

    public FontScaleTests(UiTestSession ui) => _ui = ui;

    // ---- the dials -------------------------------------------------------------------------------

    [Fact]
    public Task The_grid_dial_moves_the_grids_sizes() => _ui.Run(() =>
    {
        FontScale.ApplyGrid(17);

        Assert.Equal(17, ResultGridChrome.FontSize);
        Assert.Equal(17, ResultGridChrome.CellFontSize);
        // The header stays a point under the values, as it was when both were literals.
        Assert.Equal(16, ResultGridChrome.HeaderFontSize);
    });

    [Fact]
    public Task The_row_height_follows_the_grid_font() => _ui.Run(() =>
    {
        // Density and size are one dial, not two settings that fight (#30): a bigger font in a fixed 26px row
        // clips, and a smaller one leaves the rows looking padded.
        FontScale.ApplyGrid(13);
        var atDefault = ResultGridChrome.RowMinHeight;
        FontScale.ApplyGrid(20);
        var atLarge = ResultGridChrome.RowMinHeight;

        Assert.Equal(26, atDefault);   // unchanged from the constant it replaced
        Assert.True(atLarge > atDefault, $"{atLarge} is not taller than {atDefault}");
        Assert.True(atLarge >= 20, "the row is shorter than the text it has to hold");
    });

    [Fact]
    public Task The_chrome_dial_keeps_the_hierarchy() => _ui.Run(() =>
    {
        // Raising one dial must not flatten caption, small and body into one size — the sizes are relative to
        // it, which is the whole reason a single setting can cover the chrome.
        FontScale.ApplyUi(16);

        var caption = FontScale.Get("Font.Caption", 0);
        var small = FontScale.Get("Font.Small", 0);
        var body = FontScale.Get("Font.Body", 0);

        Assert.Equal(16, body);
        Assert.True(caption < small && small < body, $"{caption} / {small} / {body} is not a scale");
    });

    [Fact]
    public Task A_dial_below_the_floor_is_clamped_rather_than_honoured() => _ui.Run(() =>
    {
        // A settings file can be edited by hand, and a 2pt grid is not a preference — it is an unusable app.
        FontScale.ApplyGrid(1);
        Assert.Equal(FontScale.MinSize, ResultGridChrome.FontSize);

        FontScale.ApplyGrid(400);
        Assert.Equal(FontScale.MaxSize, ResultGridChrome.FontSize);
    });

    // ---- the tokens reach real visuals -----------------------------------------------------------

    [Fact]
    public Task A_realized_cell_renders_at_the_grid_size() => _ui.Run(() =>
    {
        // The claim that matters: not that the token changed, but that a cell built from it did. The grid's
        // visuals are built in code and pin FontSize per cell (#73), so this is the only place the wiring is
        // observable.
        FontScale.ApplyGrid(18);
        var result = ResultsHarness.SingleColumn("id", "int4", typeof(int), primaryKey: false, 1, 2, 3);
        var (window, view) = ResultsHarness.Show(result);

        var cell = ResultsHarness.RequireCell(window, result.Rows[0], 0);
        Assert.Equal(18, ResultsHarness.CellText(cell).FontSize);

        var header = view.GetVisualDescendants().OfType<DataGridColumnHeader>().First();
        Assert.Equal(17, header.FontSize);
        window.Close();
    });

    [Fact]
    public Task Rows_get_taller_when_the_grid_font_does() => _ui.Run(() =>
    {
        var small = Measure(11);
        var large = Measure(20);

        Assert.True(large > small, $"a 20pt grid's rows ({large}) are not taller than an 11pt grid's ({small})");

        static double Measure(int size)
        {
            FontScale.ApplyGrid(size);
            var result = ResultsHarness.SingleColumn("id", "int4", typeof(int), primaryKey: false, 1, 2, 3);
            var (window, view) = ResultsHarness.Show(result);
            var row = view.GetVisualDescendants().OfType<DataGridRow>().First();
            var height = row.Bounds.Height;
            window.Close();
            return height;
        }
    });

    [Fact]
    public Task Re_rendering_at_a_new_size_keeps_the_result() => _ui.Run(() =>
    {
        // Changing a font size is the same grid at a different size, not a new result — throwing away the
        // selection would be a surprise, and the settings window applies edits as the user moves the dial.
        FontScale.ApplyGrid(12);
        var result = ResultsHarness.SingleColumn("id", "int4", typeof(int), primaryKey: false, 1, 2, 3);
        var (window, view) = ResultsHarness.Show(result);

        FontScale.ApplyGrid(19);
        view.RefreshTypeScale();
        ResultsHarness.Pump(window);

        Assert.Equal(19, ResultsHarness.CellText(ResultsHarness.RequireCell(window, result.Rows[0], 0)).FontSize);
        Assert.Same(result, view.Results!.Single());
        window.Close();
    });

    // ---- the cache -------------------------------------------------------------------------------

    [Fact]
    public Task A_written_size_is_read_back_immediately() => _ui.Run(() =>
    {
        // The lookup is cached because it happens per cell while a grid is built. A cache that had to be
        // invalidated separately would serve the old size for the render that follows the change.
        FontScale.ApplyGrid(14);
        Assert.Equal(14, FontScale.Get("Font.Grid", 0));

        FontScale.ApplyGrid(15);
        Assert.Equal(15, FontScale.Get("Font.Grid", 0));
    });

    [Fact]
    public Task A_missing_token_falls_back_rather_than_throwing() => _ui.Run(() =>
    {
        // A missing token must not take a window down while it is being built.
        Assert.Equal(42, FontScale.Get("Font.NoSuchThing", 42));
        // And the fallback is not then cached as though it were the token's value for another caller.
        Assert.Equal(7, FontScale.Get("Font.AlsoMissing", 7));
    });

    // ---- the settings ----------------------------------------------------------------------------

    [Fact]
    public void Both_dials_are_settable_searchable_and_bounded()
    {
        // The issue's actual complaint: the grid and the panels had no setting at all.
        var grid = Assert.IsType<IntSetting>(SettingsCatalog.Find("results.gridFontSize"));
        var ui = Assert.IsType<IntSetting>(SettingsCatalog.Find("general.uiFontSize"));

        Assert.Equal(13, grid.Get(new AppSettings()));
        Assert.Equal(12, ui.Get(new AppSettings()));
        foreach (var setting in new[] { grid, ui })
        {
            Assert.Equal(FontScale.MinSize, setting.Min);
            Assert.Equal(FontScale.MaxSize, setting.Max);
            Assert.Contains("font", setting.Keywords);
            // No AppliesNote: the window applies edits immediately, and these do (see App's Changed hook),
            // so a note would be a promise broken in the other direction.
            Assert.Null(setting.AppliesNote);
        }
    }

    [Fact]
    public void Setting_a_dial_round_trips_through_the_descriptor()
    {
        var grid = (IntSetting)SettingsCatalog.Find("results.gridFontSize")!;

        var updated = grid.Set(new AppSettings(), 18);

        Assert.Equal(18, updated.GridFontSize);
        // And nothing else moved: the editor keeps its own dial.
        Assert.Equal(new AppSettings().EditorFontSize, updated.EditorFontSize);
    }
}
