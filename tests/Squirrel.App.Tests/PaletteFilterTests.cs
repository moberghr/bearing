using System.Linq;
using Avalonia.Input;
using Squirrel.App.Input;
using Xunit;

namespace Squirrel.App.Tests;

public class PaletteFilterTests
{
    private static readonly KeyCommand[] Commands =
    {
        KeyCommand.Sync("run", "Run", KeyScope.Global, "Query", () => { }),
        KeyCommand.Sync("file.save", "Save", KeyScope.Global, "File", () => { }),
        KeyCommand.Sync("file.saveAs", "Save As…", KeyScope.Global, "File", () => { }),
        KeyCommand.Sync("tab.new", "New tab", KeyScope.Global, "File", () => { }),
    };

    [Fact]
    public void Empty_query_returns_all_grouped_then_alphabetical()
    {
        var r = PaletteFilter.Rank(Commands, "");
        Assert.Equal(4, r.Count);
        // File group first (New tab, Save, Save As…), then Query (Run)
        Assert.Equal(new[] { "New tab", "Save", "Save As…", "Run" }, r.Select(c => c.Title).ToArray());
    }

    [Fact]
    public void Fuzzy_query_keeps_only_subsequence_matches_best_first()
    {
        var r = PaletteFilter.Rank(Commands, "save");
        Assert.Equal(new[] { "Save", "Save As…" }, r.Select(c => c.Title).ToArray()); // exact "Save" outranks the longer title
    }

    [Fact]
    public void Non_matching_query_returns_nothing()
        => Assert.Empty(PaletteFilter.Rank(Commands, "zzz"));

    [Fact]
    public void Score_is_null_only_when_not_a_subsequence()
    {
        Assert.NotNull(PaletteFilter.Score("Run", "run"));
        Assert.NotNull(PaletteFilter.Score("Save As…", "sa"));
        Assert.Null(PaletteFilter.Score("Run", "xq"));
    }

    [Fact]
    public void A_prefix_word_boundary_match_outscores_a_scattered_one()
        => Assert.True(PaletteFilter.Score("New tab", "nt") > PaletteFilter.Score("Current about", "nt"));
}

public class Phase3BindingTests
{
    private const PhysicalKey NoPhys = PhysicalKey.None;
    private static readonly Keymap Defaults = KeymapDefaults.Build();

    [Fact]
    public void Palette_and_tab_navigation_are_bound()
    {
        Assert.Equal(CommandIds.PaletteOpen, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control | KeyModifiers.Shift, Key.P, NoPhys));
        Assert.Equal(CommandIds.TabNext, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control, Key.Tab, NoPhys));
        Assert.Equal(CommandIds.TabNext, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control, Key.PageDown, NoPhys));
        Assert.Equal(CommandIds.TabPrev, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control | KeyModifiers.Shift, Key.Tab, NoPhys));
        Assert.Equal(CommandIds.TabPrev, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control, Key.PageUp, NoPhys));
        Assert.Equal(CommandIds.FocusCycle, Defaults.Resolve(KeyScope.Global, KeyModifiers.None, Key.F6, NoPhys));
    }

    [Fact]
    public void Fk_navigation_is_bound_in_the_grid()
    {
        Assert.Equal(CommandIds.GridFollowFk, Defaults.Resolve(KeyScope.Grid, KeyModifiers.Alt, Key.Right, NoPhys));
        Assert.Equal(CommandIds.GridBack, Defaults.Resolve(KeyScope.Grid, KeyModifiers.Alt, Key.Left, NoPhys));
        // plain arrows stay unbound (grid navigation handles them locally)
        Assert.Null(Defaults.Resolve(KeyScope.Grid, KeyModifiers.None, Key.Right, NoPhys));
    }

    [Fact]
    public void Palette_only_commands_are_unbound_but_still_exist_as_ids()
    {
        Assert.Null(Defaults.DisplayGesture(CommandIds.PanelConnections));
        Assert.Null(Defaults.DisplayGesture(CommandIds.ConnectionNew));
        Assert.Null(Defaults.DisplayGesture(CommandIds.QueryRunAll));
    }
}
