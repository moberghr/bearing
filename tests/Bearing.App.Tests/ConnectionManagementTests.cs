using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Connection management (#56, #57): inline rename, duplicate, clipboard copy/paste, and opening a script
/// against a chosen connection. The security-shaped assertions here are the point of the file — a duplicate
/// must carry its password, and the clipboard must never carry one.
/// </summary>
public class ConnectionManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-connmgmt", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private static ConnectionInfo Conn(string name, string? folder = null, string host = "db.example",
                                       int port = 5433, string user = "karlo")
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            ProviderId = "postgres",
            Host = host,
            Port = port,
            Database = "app",
            User = user,
            Folder = folder,
            Environment = "production",
            EnvironmentColor = "#E5484D",
            RequireWriteConfirmation = true,
        };

    private (WorkspaceContext ctx, ConnectionsViewModel vm, FakeSecretStore secrets) NewVm(
        params ConnectionInfo[] connections)
    {
        Directory.CreateDirectory(_root);
        var secrets = new FakeSecretStore();
        var ctx = new WorkspaceContext(
            new FakeProvider(),
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            secrets);
        var manifest = new ProjectManifest();
        manifest.Connections.AddRange(connections);
        ctx.Project = new Project { Directory = _root, Manifest = manifest };
        var vm = new ConnectionsViewModel(ctx);
        vm.RefreshConnections();
        return (ctx, vm, secrets);
    }

    private static ConnectionInfo Named(WorkspaceContext ctx, string name)
        => ctx.Project!.Manifest.Connections.Single(c => c.Name == name);

    // ---- rename --------------------------------------------------------------------------------

    [Fact]
    public async Task Renaming_a_connection_updates_the_manifest_and_the_row()
    {
        var conn = Conn("prod");
        var (ctx, vm, _) = NewVm(conn);

        await vm.RenameConnectionAsync(conn.Id, "  production  ");

        Assert.Equal("production", Named(ctx, "production").Name);
        Assert.Equal("production", vm.ServerNodes.OfType<ServerNodeViewModel>().Single().Title);
    }

    [Fact]
    public async Task Renaming_keeps_the_node_and_everything_it_loaded()
    {
        var conn = Conn("prod");
        var (_, vm, _) = NewVm(conn);
        var before = vm.ServerNodes.OfType<ServerNodeViewModel>().Single();
        before.IsExpanded = true;

        await vm.RenameConnectionAsync(conn.Id, "production");

        // A rename changes nothing about the server, so SameNetwork holds and the node is reused.
        Assert.Same(before, vm.ServerNodes.OfType<ServerNodeViewModel>().Single());
        Assert.True(before.IsExpanded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Renaming_to_blank_is_refused(string name)
    {
        var conn = Conn("prod");
        var (ctx, vm, _) = NewVm(conn);

        await vm.RenameConnectionAsync(conn.Id, name);

        Assert.Equal("prod", Named(ctx, "prod").Name);
    }

    [Fact]
    public void BeginRename_seeds_the_box_from_the_current_name()
    {
        var (_, vm, _) = NewVm(Conn("prod"));
        var node = vm.ServerNodes.OfType<ServerNodeViewModel>().Single();

        node.BeginRename();

        Assert.True(node.IsRenaming);
        Assert.Equal("prod", node.RenameDraft);
    }

    // ---- duplicate -----------------------------------------------------------------------------

    [Fact]
    public async Task Duplicating_copies_the_settings_under_a_new_identity()
    {
        var conn = Conn("prod", folder: "Aur");
        var (ctx, vm, _) = NewVm(conn);

        await vm.DuplicateConnectionAsync(conn.Id);

        var copy = Named(ctx, "prod (copy)");
        Assert.NotEqual(conn.Id, copy.Id);
        Assert.Equal(conn.Host, copy.Host);
        Assert.Equal(conn.Port, copy.Port);
        Assert.Equal(conn.User, copy.User);
        Assert.Equal("Aur", copy.Folder);                       // lands beside its source
        Assert.True(copy.RequireWriteConfirmation);             // and keeps the guard
        Assert.Equal("#E5484D", copy.EnvironmentColor);
    }

    [Fact]
    public async Task Duplicating_copies_the_stored_password_to_the_new_id()
    {
        var conn = Conn("prod");
        var (ctx, vm, secrets) = NewVm(conn);
        await secrets.SetPasswordAsync(conn.Id, "hunter2", CancellationToken.None);

        await vm.DuplicateConnectionAsync(conn.Id);

        // A copy that cannot connect until you retype the credential is half a feature. Both entries are
        // the same user's, in the same OS keychain — the secret never leaves it.
        var copy = Named(ctx, "prod (copy)");
        Assert.Equal("hunter2", await secrets.GetPasswordAsync(copy.Id, CancellationToken.None));
        Assert.Equal("hunter2", await secrets.GetPasswordAsync(conn.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Duplicating_a_connection_with_no_stored_password_still_works()
    {
        var conn = Conn("prod");
        var (ctx, vm, secrets) = NewVm(conn);

        await vm.DuplicateConnectionAsync(conn.Id);

        Assert.Equal(2, ctx.Project!.Manifest.Connections.Count);
        Assert.Null(await secrets.GetPasswordAsync(Named(ctx, "prod (copy)").Id, CancellationToken.None));
    }

    [Fact]
    public async Task Duplicating_twice_numbers_the_copies()
    {
        var conn = Conn("prod");
        var (ctx, vm, _) = NewVm(conn);

        await vm.DuplicateConnectionAsync(conn.Id);
        await vm.DuplicateConnectionAsync(conn.Id);

        Assert.Equal(3, ctx.Project!.Manifest.Connections.Count);
        Assert.Contains(ctx.Project.Manifest.Connections, c => c.Name == "prod (copy)");
        Assert.Contains(ctx.Project.Manifest.Connections, c => c.Name == "prod (copy 2)");
    }

    // ---- opening a script against a connection (#57) -------------------------------------------

    [Fact]
    public void A_new_tab_can_be_pointed_at_a_chosen_connection()
    {
        var a = Conn("a");
        var b = Conn("b");
        var (ctx, vm, _) = NewVm(a, b);
        var workspace = new WorkspaceViewModel(ctx, new ScriptsViewModel(ctx, () => { }), vm);
        workspace.NewTab();                       // a first tab, inheriting nothing in particular

        var tab = workspace.NewTab(connectionId: b.Id);

        Assert.Equal(b.Id, tab.ConnectionId);
        Assert.Equal("b", tab.ConnectionDisplay);
    }

    [Fact]
    public void A_new_tab_still_inherits_when_no_connection_is_named()
    {
        var a = Conn("a");
        var (ctx, vm, _) = NewVm(a);
        var workspace = new WorkspaceViewModel(ctx, new ScriptsViewModel(ctx, () => { }), vm);
        workspace.NewTab(connectionId: a.Id);

        var next = workspace.NewTab();

        // The default path is unchanged: a tab opened from the editor keeps working against the same server.
        Assert.Equal(a.Id, next.ConnectionId);
    }

    [Fact]
    public void A_deep_row_reports_the_connection_it_sits_under()
    {
        var conn = Conn("prod");
        var (_, vm, _) = NewVm(conn);
        var server = vm.ServerNodes.OfType<ServerNodeViewModel>().Single();

        // Ctrl+N with a table selected should still find the server (#57), so the walk-up has to work from
        // any depth rather than only from the server row itself.
        Assert.Equal(conn.Id, server.OwningConnection?.Id);
    }

    [Fact]
    public async Task A_folder_row_belongs_to_no_connection()
    {
        var (_, vm, _) = NewVm(Conn("prod", folder: "Aur"));
        var folder = vm.ServerNodes.OfType<ConnectionFolderNodeViewModel>().Single();

        Assert.Null(folder.OwningConnection);
        Assert.Equal("prod", folder.Children.OfType<ServerNodeViewModel>().Single().OwningConnection?.Name);
        await Task.CompletedTask;
    }

    // ---- clipboard -----------------------------------------------------------------------------

    [Fact]
    public void The_clipboard_payload_carries_no_password_and_no_id()
    {
        var conn = Conn("prod");
        var (_, vm, _) = NewVm(conn);

        var text = vm.CopyToClipboardText(conn.Id)!;

        // The clipboard is readable by every process on the machine and outlives the copy in clipboard
        // managers — the opposite of §1.1. The id is excluded too: it is the secret-store lookup key.
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(conn.Id.ToString(), text);
        Assert.Contains("prod", text);
        Assert.Contains("db.example", text);
    }

    [Fact]
    public void Copying_an_unknown_connection_yields_nothing_to_put_on_the_clipboard()
    {
        var (_, vm, _) = NewVm(Conn("prod"));
        Assert.Null(vm.CopyToClipboardText(Guid.NewGuid()));
    }

    [Fact]
    public async Task A_copied_connection_pastes_back_as_a_distinct_connection()
    {
        var conn = Conn("prod");
        var (ctx, vm, _) = NewVm(conn);
        var text = vm.CopyToClipboardText(conn.Id);

        var pasted = await vm.PasteFromClipboardTextAsync(text);

        Assert.Equal(1, pasted);
        var copy = Named(ctx, "prod (copy)");
        Assert.NotEqual(conn.Id, copy.Id);
        Assert.Equal(conn.Host, copy.Host);
        Assert.Equal(conn.Port, copy.Port);
        Assert.True(copy.RequireWriteConfirmation);
    }

    [Fact]
    public async Task Pasting_into_a_project_that_has_never_seen_it_keeps_the_name()
    {
        var (source, sourceVm, _) = NewVm(Conn("prod"));
        var text = sourceVm.CopyToClipboardText(source.Project!.Manifest.Connections[0].Id);

        // A second project, standing in for a colleague pasting the payload from a chat message.
        source.Project.Manifest.Connections.Clear();
        sourceVm.RefreshConnections();

        await sourceVm.PasteFromClipboardTextAsync(text);

        Assert.Equal("prod", source.Project.Manifest.Connections.Single().Name);
    }

    [Fact]
    public async Task Pasting_onto_a_folder_files_it_there_regardless_of_where_it_came_from()
    {
        var conn = Conn("prod", folder: "Aur");
        var (ctx, vm, _) = NewVm(conn);
        var text = vm.CopyToClipboardText(conn.Id);

        await vm.PasteFromClipboardTextAsync(text, "Netgiro", overrideFolder: true);

        Assert.Equal("Netgiro", Named(ctx, "prod (copy)").Folder);
    }

    [Fact]
    public async Task Pasting_without_a_target_keeps_the_folder_the_payload_carried()
    {
        var conn = Conn("prod", folder: "Aur");
        var (ctx, vm, _) = NewVm(conn);
        var text = vm.CopyToClipboardText(conn.Id);

        await vm.PasteFromClipboardTextAsync(text);

        Assert.Equal("Aur", Named(ctx, "prod (copy)").Folder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just some text")]
    [InlineData("{\"not\":\"ours\"}")]
    [InlineData("{ broken json")]
    public async Task Pasting_something_that_is_not_ours_does_nothing(string? text)
    {
        var (ctx, vm, _) = NewVm(Conn("prod"));

        var pasted = await vm.PasteFromClipboardTextAsync(text);

        // A paste gesture over unrelated clipboard content should do nothing, not something surprising.
        Assert.Equal(0, pasted);
        Assert.Single(ctx.Project!.Manifest.Connections);
    }

    [Fact]
    public async Task Several_connections_round_trip_in_one_payload()
    {
        var (ctx, vm, _) = NewVm(Conn("a"), Conn("b"), Conn("c"));
        var ids = ctx.Project!.Manifest.Connections.Take(2).Select(c => c.Id).ToArray();
        var text = vm.CopyToClipboardText(ids);

        var pasted = await vm.PasteFromClipboardTextAsync(text);

        Assert.Equal(2, pasted);
        Assert.Equal(5, ctx.Project.Manifest.Connections.Count);
    }
}

/// <summary>
/// The clipboard payload itself (#56) — pure, so the round trip is covered without a clipboard or a window.
/// </summary>
public class ConnectionClipboardTests
{
    private static ConnectionInfo Conn() => new()
    {
        Id = Guid.NewGuid(),
        Name = "prod",
        ProviderId = "postgres",
        Host = "db.example",
        Port = 5433,
        Database = "app",
        User = "karlo",
        Folder = "Aur/Production",
        Environment = "production",
        EnvironmentColor = "#E5484D",
        RequireWriteConfirmation = true,
        // A mode set on the *field*, which is what the dialog writes. The bag entry stays too, because the
        // legacy path still has to work — but it is deliberately a different mode from the field, so a
        // payload that dropped the field could not pass by accidentally resolving the bag instead. That is
        // exactly how the omission below went unnoticed.
        Tls = TlsMode.VerifyFull,
        ReadOnly = true,
        StatementTimeoutSeconds = 30,
        CredentialKind = CredentialKind.EntraToken,
        Options = new Dictionary<string, string> { ["sslmode"] = "require" },
    };

    /// <summary>
    /// Every non-secret field, established <b>by construction</b> rather than by a hand-written list.
    /// <para>
    /// The list version of this test named ten fields and passed for months while the payload silently
    /// dropped <c>Tls</c> — copying a Verify Full connection pasted one on the driver default. It could not
    /// have caught that: the omitted field was omitted from the assertions too. Reflecting over the record
    /// means the next field added to <see cref="ConnectionInfo"/> fails here until it is carried, which is
    /// the only version of this test that does what its name says.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_non_secret_field_survives_the_round_trip()
    {
        var source = Conn();

        Assert.True(ConnectionClipboard.TryRead(ConnectionClipboard.Write(new[] { source }), out var read));

        var got = read.Single();

        // Id is the one deliberate exception: it is the secret-store lookup key, and a payload that could
        // express one could leak one. The_id_is_never_carried_across covers that directly.
        var carried = typeof(ConnectionInfo).GetProperties()
            .Where(p => p.Name != nameof(ConnectionInfo.Id))
            .ToList();
        Assert.NotEmpty(carried);

        foreach (var property in carried)
        {
            var expected = property.GetValue(source);
            var actual = property.GetValue(got);

            if (property.Name == nameof(ConnectionInfo.Options))
            {
                // The bag is a dictionary, so compare its entries rather than its reference.
                Assert.Equal("require", ((IReadOnlyDictionary<string, string>)actual!)["sslmode"]);
                continue;
            }

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void A_copied_connection_keeps_the_encryption_and_safety_settings_it_had()
    {
        // Named separately because it is the bug rather than the invariant: these three decide whether the
        // session is encrypted (#23) and whether the server will accept a write on it (#99 / #105), so a
        // paste that quietly relaxed any of them is the worst outcome copy/paste has.
        var source = Conn();

        Assert.True(ConnectionClipboard.TryRead(ConnectionClipboard.Write(new[] { source }), out var read));

        var got = read.Single();
        Assert.Equal(TlsMode.VerifyFull, got.Tls);
        Assert.True(got.ReadOnly);
        Assert.Equal(30, got.StatementTimeoutSeconds);
    }

    [Fact]
    public void A_connection_still_carrying_its_mode_in_the_bag_pastes_as_the_mode_it_runs_on()
    {
        // An older project (or a DBeaver import) has no Tls field set and an sslmode entry instead. Copying
        // it must not paste the untouched default: the mode it actually connects under is the resolved one.
        var legacy = Conn() with
        {
            Tls = TlsPolicy.Default,
            Options = new Dictionary<string, string> { ["sslmode"] = "verify-full" },
        };

        Assert.True(ConnectionClipboard.TryRead(ConnectionClipboard.Write(new[] { legacy }), out var read));

        Assert.Equal(TlsMode.VerifyFull, TlsPolicy.Resolve(read.Single()));
    }

    [Fact]
    public void The_id_is_never_carried_across()
    {
        var source = Conn();
        ConnectionClipboard.TryRead(ConnectionClipboard.Write(new[] { source }), out var read);

        // The id is the secret-store lookup key: reusing it would point a second connection at the first
        // one's password.
        Assert.NotEqual(source.Id, read.Single().Id);
        Assert.NotEqual(Guid.Empty, read.Single().Id);
    }

    [Fact]
    public void Two_reads_of_one_payload_produce_two_distinct_identities()
    {
        var text = ConnectionClipboard.Write(new[] { Conn() });

        ConnectionClipboard.TryRead(text, out var first);
        ConnectionClipboard.TryRead(text, out var second);

        Assert.NotEqual(first.Single().Id, second.Single().Id);
    }

    [Fact]
    public void A_payload_with_no_connections_is_declined()
        => Assert.False(ConnectionClipboard.TryRead(
            "{\"Kind\":\"bearing.connections\",\"Version\":1,\"Connections\":[]}", out _));

    [Fact]
    public void Json_from_somewhere_else_is_declined_even_with_the_right_fields()
        => Assert.False(ConnectionClipboard.TryRead(
            "{\"Kind\":\"other.app\",\"Version\":1,\"Connections\":[{\"Name\":\"x\"}]}", out _));

    [Fact]
    public void A_payload_missing_fields_falls_back_rather_than_throwing()
    {
        Assert.True(ConnectionClipboard.TryRead(
            "{\"Kind\":\"bearing.connections\",\"Version\":1,\"Connections\":[{\"Host\":\"h\"}]}", out var read));

        var got = read.Single();
        Assert.Equal("Connection", got.Name);
        Assert.Equal("postgres", got.ProviderId);
        Assert.Equal(5432, got.Port);
    }

    [Fact]
    public void Folder_paths_are_normalised_on_the_way_in()
    {
        Assert.True(ConnectionClipboard.TryRead(
            "{\"Kind\":\"bearing.connections\",\"Version\":1,\"Connections\":[{\"Name\":\"x\",\"Folder\":\" /Aur/ \"}]}",
            out var read));

        Assert.Equal("Aur", read.Single().Folder);
    }
}
