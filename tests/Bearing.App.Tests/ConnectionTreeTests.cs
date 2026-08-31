using System;
using System.Linq;
using Bearing.App.Connections;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The connections panel's shape (#80): folder nesting, inference, ordering, and how the filter treats
/// folders. Pure, so all of it is testable without a tree control or a live server (§2.5, §4.3) — which is
/// the whole reason the builder was extracted.
/// </summary>
public class ConnectionTreeTests
{
    private static ConnectionInfo Conn(string name, string? folder = null, string host = "localhost",
                                       int port = 5432, string? environment = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            ProviderId = "postgres",
            Host = host,
            Port = port,
            Database = "app",
            Folder = folder,
            Environment = environment,
        };

    private static ConnectionTree.Folder Sub(ConnectionTree.Folder parent, string name)
        => parent.Folders.Single(f => f.Name == name);

    // ---- structure -----------------------------------------------------------------------------

    [Fact]
    public void Connections_with_no_folder_sit_at_the_root()
    {
        var root = ConnectionTree.Build(new[] { Conn("a"), Conn("b") });

        Assert.Empty(root.Folders);
        Assert.Equal(new[] { "a", "b" }, root.Connections.Select(c => c.Name));
    }

    [Fact]
    public void A_connections_folder_is_created_even_when_nobody_declared_it()
    {
        var root = ConnectionTree.Build(new[] { Conn("prod", "Aur Production") });

        // A hand-edited project.json, or an import that wrote membership without declaring folders, must
        // never leave a connection filed somewhere the panel doesn't draw.
        Assert.Equal("prod", Sub(root, "Aur Production").Connections.Single().Name);
        Assert.Empty(root.Connections);
    }

    [Fact]
    public void A_declared_folder_survives_with_nothing_in_it()
    {
        var root = ConnectionTree.Build(Array.Empty<ConnectionInfo>(), new[] { "Aur Staging" });

        // Otherwise you could not create a folder before filing anything into it.
        Assert.Equal("Aur Staging", root.Folders.Single().Name);
        Assert.Equal(0, root.Folders.Single().Count);
    }

    [Fact]
    public void Nesting_follows_the_path_and_implies_the_levels_above_it()
    {
        var root = ConnectionTree.Build(new[] { Conn("prod", "Clients/Aur/Production") });

        var clients = Sub(root, "Clients");
        var aur = Sub(clients, "Aur");
        Assert.Equal("prod", Sub(aur, "Production").Connections.Single().Name);
    }

    [Fact]
    public void Count_is_recursive_so_a_collapsed_folder_says_what_it_hides()
    {
        var root = ConnectionTree.Build(new[]
        {
            Conn("a", "Aur"),
            Conn("b", "Aur/Production"),
            Conn("c", "Aur/Staging"),
            Conn("d"),
        });

        Assert.Equal(3, Sub(root, "Aur").Count);
        Assert.Equal(4, root.Count);
    }

    [Fact]
    public void Paths_differing_only_by_case_are_one_folder()
    {
        var root = ConnectionTree.Build(new[] { Conn("a", "Aur"), Conn("b", "aur") });

        Assert.Single(root.Folders);
        Assert.Equal(2, root.Folders.Single().Connections.Count);
    }

    // ---- ordering ------------------------------------------------------------------------------

    [Fact]
    public void Folders_sort_before_connections_and_both_alphabetically()
    {
        var root = ConnectionTree.Build(
            new[] { Conn("zeta"), Conn("alpha"), Conn("x", "Zoo"), Conn("y", "Ant") });

        Assert.Equal(new[] { "Ant", "Zoo" }, root.Folders.Select(f => f.Name));
        Assert.Equal(new[] { "alpha", "zeta" }, root.Connections.Select(c => c.Name));
    }

    [Fact]
    public void Ordering_ignores_case_rather_than_putting_lowercase_last()
    {
        var root = ConnectionTree.Build(new[] { Conn("beta"), Conn("Alpha"), Conn("gamma") });
        Assert.Equal(new[] { "Alpha", "beta", "gamma" }, root.Connections.Select(c => c.Name));
    }

    // ---- filter --------------------------------------------------------------------------------

    [Fact]
    public void Filtering_keeps_a_folder_that_still_contains_a_hit()
    {
        var root = ConnectionTree.Build(
            new[] { Conn("netgiro prod", "Netgiro"), Conn("aur prod", "Aur") }, filter: "netgiro");

        Assert.Equal("Netgiro", root.Folders.Single().Name);
        Assert.Equal("netgiro prod", root.Folders.Single().Connections.Single().Name);
    }

    [Fact]
    public void Filtering_drops_a_folder_that_can_no_longer_contain_one()
    {
        var root = ConnectionTree.Build(
            new[] { Conn("a", "Aur") }, new[] { "Empty" }, filter: "zzz");

        Assert.Empty(root.Folders);   // a declared-but-empty folder is noise while searching
    }

    [Fact]
    public void A_folder_matched_by_name_keeps_everything_in_it()
    {
        var root = ConnectionTree.Build(new[]
        {
            Conn("prod aur", "Aur Production"),
            Conn("legacy", "Aur Production"),
            Conn("elsewhere", "Netgiro"),
        }, filter: "Aur Production");

        // Having asked for the folder by name you want its contents, not just the rows that happen to
        // repeat the folder's name.
        var folder = root.Folders.Single();
        Assert.Equal(new[] { "legacy", "prod aur" }, folder.Connections.Select(c => c.Name));
    }

    [Fact]
    public void A_matched_folder_keeps_its_subfolders_too()
    {
        var root = ConnectionTree.Build(
            new[] { Conn("x", "Aur/Production"), Conn("y", "Aur/Staging") }, filter: "Aur");

        var aur = Sub(root, "Aur");
        Assert.Equal(new[] { "Production", "Staging" }, aur.Folders.Select(f => f.Name));
    }

    [Fact]
    public void The_filter_still_matches_a_connection_on_its_port_inside_a_folder()
    {
        var root = ConnectionTree.Build(
            new[] { Conn("one", "Aur", port: 5432), Conn("two", "Aur", port: 5434) }, filter: "5434");

        Assert.Equal("two", Sub(root, "Aur").Connections.Single().Name);
    }

    [Fact]
    public void An_empty_filter_shows_everything_including_empty_folders()
    {
        var root = ConnectionTree.Build(new[] { Conn("a") }, new[] { "Empty" }, filter: "   ");

        Assert.Equal("Empty", root.Folders.Single().Name);
        Assert.Single(root.Connections);
    }
}
