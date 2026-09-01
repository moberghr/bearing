using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Bearing.App.Theming;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The <i>chrome</i> half of the type scale (#52) — the gap the coverage audit found.
/// <para>
/// About thirty font-size literals became tokens: twenty <c>FontSize="{DynamicResource Font.*}"</c> in
/// <c>SidebarView.axaml</c> and <c>MainWindow.axaml</c>, and twelve <c>Metric(…)</c> calls in the code-built
/// result chrome. Only the <b>grid</b> was tested. A change that reverted any of the others to a literal —
/// or a new control added to a panel with a fresh one — would have passed the whole suite while quietly
/// ignoring the setting, which is precisely the rot the token layer exists to prevent.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class ChromeTypeScaleTests
{
    private readonly UiTestSession _ui;

    public ChromeTypeScaleTests(UiTestSession ui) => _ui = ui;

    [Fact]
    public Task Every_tokenised_panel_label_follows_the_interface_dial() => _ui.Run(async () =>
    {
        // The labels the token layer actually owns. Checked as a set rather than one example, because the rot
        // this guards against is a *new* control arriving with a fresh literal.
        using var shell = await ShellHarness.ShowAsync(nameof(Every_tokenised_panel_label_follows_the_interface_dial));

        FontScale.ApplyUi(12);
        shell.Pump();
        var atDefault = Labelled(shell);

        FontScale.ApplyUi(19);
        shell.Pump();
        var raised = Labelled(shell);

        Assert.NotEmpty(atDefault);
        Assert.Equal(atDefault.Keys.OrderBy(k => k), raised.Keys.OrderBy(k => k));
        foreach (var (text, before) in atDefault)
            Assert.True(raised[text] > before, $"'{text}' stayed at {before} while the dial went to 19");
    });

    [Fact]
    public Task Stock_controls_keep_the_themes_own_size_and_that_is_the_boundary() => _ui.Run(async () =>
    {
        // Found by the audit, and worth pinning rather than quietly fixing: the dial reaches the labels that
        // were literals, and not the Buttons, TextBoxes and ComboBoxes around them, which take Fluent's 14.
        //
        // So a dial at 19 gives a 19px "No connections yet." above 14px buttons. Unifying the two is a design
        // change, not a bug fix — Font.Body defaults to 12 and the controls to 14, so making one setting drive
        // both would resize the whole app. This assertion exists so that change is deliberate when it happens,
        // and so nobody reads #52 as covering more than it does.
        using var shell = await ShellHarness.ShowAsync(nameof(Stock_controls_keep_the_themes_own_size_and_that_is_the_boundary));

        FontScale.ApplyUi(19);
        shell.Pump();

        var button = shell.Window.GetVisualDescendants()
            .OfType<Button>()
            .First(b => b.Content as string == "Explore demo data");
        Assert.Equal(14, button.FontSize);
        Assert.NotEqual(FontScale.Get("Font.Body", 0), button.FontSize);
    });

    [Fact]
    public Task A_named_chrome_label_lands_on_the_token_it_binds() => _ui.Run(async () =>
    {
        // A specific control rather than a set, so a failure says which token drifted. The panel header binds
        // Font.Small, which sits one point under the dial.
        using var shell = await ShellHarness.ShowAsync(nameof(A_named_chrome_label_lands_on_the_token_it_binds));

        FontScale.ApplyUi(18);
        shell.Pump();

        var header = shell.Window.GetVisualDescendants()
            .OfType<TextBlock>()
            .First(t => t.Text == "CONNECTIONS");
        Assert.Equal(FontScale.Get("Font.Small", 0), header.FontSize);
        Assert.Equal(17, header.FontSize);
    });

    [Fact]
    public Task The_result_meta_row_follows_it_too() => _ui.Run(() =>
    {
        // The code-built half: these read a token once through Tokens.Metric rather than binding it, so they
        // are the ones a dial change cannot reach on its own — the results view is re-rendered for them.
        FontScale.ApplyUi(17);
        var result = ResultsHarness.SingleColumn("id", "int4", typeof(int), primaryKey: false, 1, 2);
        var (window, view) = ResultsHarness.Show(result);

        var meta = view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.Text is not null && t.Text.Contains("rows"))
            .ToList();

        Assert.NotEmpty(meta);
        Assert.All(meta, t => Assert.Equal(FontScale.Get("Font.Body", 0), t.FontSize));
        window.Close();
    });

    [Fact]
    public Task The_chrome_dial_and_the_grid_dial_are_independent() => _ui.Run(() =>
    {
        // Two dials was the design decision (#52): the grid is the surface you stare at longest and wants its
        // own size. Moving one must not move the other.
        FontScale.Apply(uiSize: 12, gridSize: 13);
        FontScale.ApplyUi(20);

        Assert.Equal(13, FontScale.Get("Font.Grid", 0));
        Assert.Equal(20, FontScale.Get("Font.Body", 0));

        FontScale.ApplyGrid(9);
        Assert.Equal(20, FontScale.Get("Font.Body", 0));
    });

    /// <summary>
    /// The panel labels the token layer owns, keyed by their text. Buttons, text boxes and combo boxes are
    /// excluded deliberately — see <see cref="Stock_controls_keep_the_themes_own_size_and_that_is_the_boundary"/>.
    /// </summary>
    private static System.Collections.Generic.Dictionary<string, double> Labelled(ShellHarness shell)
        => shell.Window.GetVisualDescendants()
            .OfType<Bearing.App.Controls.SidebarView>()
            .SelectMany(s => s.GetVisualDescendants().OfType<TextBlock>())
            .Where(t => !string.IsNullOrEmpty(t.Text))
            .Where(t => t.FindAncestorOfType<Button>() is null && t.FindAncestorOfType<TextBox>() is null)
            .GroupBy(t => t.Text!)
            .ToDictionary(g => g.Key, g => g.First().FontSize);
}
