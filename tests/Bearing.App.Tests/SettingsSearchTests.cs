using System.Linq;
using Bearing.App.Settings;
using Bearing.Core.Workspace;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The settings window's search box. Matching runs against the real catalog so these also fail when a
/// setting's copy stops being findable by the words someone would actually type for it.
/// </summary>
public class SettingsSearchTests
{
    private static string[] Keys(string? query, string? category = null)
        => SettingsSearch.Filter(query, category).SelectMany(s => s.Settings).Select(d => d.Key).ToArray();

    [Fact]
    public void No_query_lists_every_section_in_declared_order()
    {
        var sections = SettingsSearch.Filter(null);

        Assert.Equal(SettingsCatalog.Categories.Select(c => c.Id), sections.Select(s => s.Category.Id));
        Assert.Equal(SettingsCatalog.All.Count, sections.Sum(s => s.Settings.Count));
    }

    [Fact]
    public void A_category_filter_keeps_only_that_section()
    {
        var sections = SettingsSearch.Filter(null, SettingsCatalog.Editor);

        Assert.Equal(SettingsCatalog.Editor, Assert.Single(sections).Category.Id);
        Assert.Equal(SettingsCatalog.InCategory(SettingsCatalog.Editor).Count, sections[0].Settings.Count);
    }

    [Fact]
    public void Sections_with_no_match_drop_out_entirely()
    {
        var sections = SettingsSearch.Filter("autosave");

        Assert.Equal(SettingsCatalog.Editor, Assert.Single(sections).Category.Id);
    }

    [Fact]
    public void A_query_matching_nothing_returns_nothing()
    {
        Assert.Empty(SettingsSearch.Filter("kubernetes"));
    }

    [Fact]
    public void Multi_word_queries_match_across_title_and_description()
    {
        Assert.Contains("connections.idleTimeoutMinutes", Keys("idle connection"));
        Assert.Contains("editor.fontSize", Keys("font size"));
    }

    [Fact]
    public void Keywords_find_a_setting_whose_title_does_not_use_the_word()
    {
        // "close" appears nowhere in the title ("…closing…") — the keyword list is what makes it findable.
        Assert.Contains("editor.confirmTabClose", Keys("close"));
        // Nor does "prune" — it's in the description of the retention setting.
        Assert.Contains("history.retentionDays", Keys("prune"));
    }

    [Fact]
    public void A_title_hit_outranks_a_description_only_hit()
    {
        // Synthetic so the rule is pinned independently of the real catalog's wording.
        var titled = Fake("s.titled", "Zoom level");
        var described = Fake("s.described", "Something else", "Controls the zoom level.");

        Assert.True(SettingsSearch.Score(titled, "zoom") > SettingsSearch.Score(described, "zoom"));

        var section = Assert.Single(SettingsSearch.Filter("zoom", null,
            [described, titled], [new SettingsCategory("s", "Synthetic")]));
        Assert.Equal(["s.titled", "s.described"], section.Settings.Select(d => d.Key));
    }

    [Fact]
    public void A_word_boundary_hit_outranks_a_mid_word_one()
    {
        Assert.True(SettingsSearch.Score(Fake("s.a", "Rows per page"), "rows")
                  > SettingsSearch.Score(Fake("s.b", "Arrows per page"), "rows"));
    }

    private static SettingDescriptor Fake(string key, string title, string description = "") => new BoolSetting
    {
        Key = key,
        CategoryId = "s",
        Title = title,
        Description = description,
        Get = s => s.ConfirmTabClose,
        Set = (s, v) => s with { ConfirmTabClose = v },
    };

    [Fact]
    public void An_abbreviated_query_still_lands_via_the_fuzzy_fallback()
    {
        // Not a substring of anything — only the subsequence scorer on the title can match this.
        Assert.Contains("editor.fontSize", Keys("fntsz"));
    }

    [Fact]
    public void Search_is_case_insensitive()
    {
        Assert.Equal(Keys("Autosave"), Keys("autosave"));
    }

    [Fact]
    public void Search_and_category_filter_compose()
    {
        Assert.Empty(SettingsSearch.Filter("autosave", SettingsCatalog.History));
        Assert.NotEmpty(SettingsSearch.Filter("autosave", SettingsCatalog.Editor));
    }
}
