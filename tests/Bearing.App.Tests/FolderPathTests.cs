using Bearing.App.Connections;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The "/"-separated folder path (#80). Pure string work, but it arrives from the UI, from a hand-edited
/// project.json and from a DBeaver import, each spelling it slightly differently — these pin the canonical
/// form so the three cannot produce folders that look identical and aren't.
/// </summary>
public class FolderPathTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("/", null)]
    [InlineData("  /  ", null)]
    [InlineData("Aur", "Aur")]
    [InlineData("  Aur  ", "Aur")]
    [InlineData("/Aur/", "Aur")]
    [InlineData("Aur / Production", "Aur/Production")]
    [InlineData("Aur//Production", "Aur/Production")]
    public void Normalize_produces_one_spelling(string? input, string? expected)
        => Assert.Equal(expected, FolderPath.Normalize(input));

    [Theory]
    [InlineData("Aur/Production", "Production")]
    [InlineData("Aur", "Aur")]
    [InlineData(null, null)]
    public void Name_is_the_last_segment(string? path, string? expected)
        => Assert.Equal(expected, FolderPath.Name(path));

    [Theory]
    [InlineData("Aur/Production", "Aur")]
    [InlineData("Aur", null)]
    [InlineData(null, null)]
    [InlineData("a/b/c", "a/b")]
    public void Parent_walks_one_level_out(string? path, string? expected)
        => Assert.Equal(expected, FolderPath.Parent(path));

    [Fact]
    public void Ancestry_materialises_a_nested_path_one_level_at_a_time()
        => Assert.Equal(new[] { "a", "a/b", "a/b/c" }, FolderPath.Ancestry("a/b/c"));

    [Fact]
    public void IsWithin_is_true_for_the_folder_itself_and_its_descendants()
    {
        Assert.True(FolderPath.IsWithin("Aur", "Aur"));
        Assert.True(FolderPath.IsWithin("Aur/Production", "Aur"));
        Assert.False(FolderPath.IsWithin("Aur", "Aur/Production"));
        Assert.False(FolderPath.IsWithin("Aurora", "Aur"));   // not a path boundary
    }

    [Fact]
    public void The_root_contains_everything()
    {
        Assert.True(FolderPath.IsWithin("Aur/Production", null));
        Assert.False(FolderPath.IsWithin(null, "Aur"));
    }

    [Fact]
    public void Rebase_moves_a_whole_subtree()
    {
        Assert.Equal("Clients/Aur/Production", FolderPath.Rebase("Aur/Production", "Aur", "Clients/Aur"));
        Assert.Equal("Clients/Aur", FolderPath.Rebase("Aur", "Aur", "Clients/Aur"));
    }

    [Fact]
    public void Rebase_to_the_root_lifts_a_subtree_out()
        => Assert.Equal("Production", FolderPath.Rebase("Aur/Production", "Aur", null));

    [Fact]
    public void Rebase_leaves_paths_outside_the_moved_folder_alone()
        => Assert.Equal("Netgiro/Sandbox", FolderPath.Rebase("Netgiro/Sandbox", "Aur", "Clients/Aur"));

    [Fact]
    public void A_typed_name_cannot_smuggle_in_a_nesting_level()
        => Assert.Equal("a-b", FolderPath.SanitizeSegment("a/b"));
}
