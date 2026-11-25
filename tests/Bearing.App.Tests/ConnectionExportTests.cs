using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Bearing.Sessions;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Export connections to a file and import them back (moving to a new machine). The point of the file is in
/// two directions at once: every setting that restricts a connection must survive it, and no secret, id or
/// exposure may be in it.
/// </summary>
public class ConnectionExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-export", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private const string Password = "hunter2-db-password";
    private const string SshSecret = "ssh-passphrase-9f1c";

    /// <summary>Every field off its default, so a file that dropped one fails the round trip rather than
    /// comparing a default with a default.</summary>
    private static ConnectionInfo Full(string name = "prod", string? folder = "Clients/Acme") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = "postgres",
        Host = "db.example",
        Port = 5433,
        Database = "app",
        User = "karlo",
        Folder = folder,
        Environment = "production",
        EnvironmentColor = "#E5484D",
        RequireWriteConfirmation = true,
        Tls = TlsMode.VerifyFull,
        ReadOnly = true,
        StatementTimeoutSeconds = 30,
        ManualCommit = true,
        ConfirmFullScans = false,
        SessionTimeZone = "UTC",
        ExternalAccess = ExternalAccess.ReadOnly,
        CredentialKind = CredentialKind.StoredPassword,
        UserFromEntraLogin = true,
        Options = new Dictionary<string, string> { ["ApplicationName"] = "bearing-test" },
        SshTunnel = new SshTunnel
        {
            Host = "bastion.example",
            Port = 2222,
            User = "jump",
            PrivateKeyPath = "~/.ssh/id_ed25519",
            HostKeyFingerprint = "SHA256:pinned",
        },
    };

    private static ConnectionInfo Plain(string name, string? folder = null, string host = "h") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = "postgres",
        Host = host,
        Database = "app",
        Folder = folder,
    };

    private static ProjectManifest Manifest(IEnumerable<ConnectionInfo> connections, params string[] folders)
    {
        var m = new ProjectManifest();
        m.Connections.AddRange(connections);
        m.ConnectionFolders.AddRange(folders);
        return m;
    }

    private (WorkspaceContext ctx, ConnectionsViewModel vm, FakeSecretStore secrets) NewVm(ProjectManifest manifest)
    {
        Directory.CreateDirectory(_root);
        var secrets = new FakeSecretStore();
        var ctx = new WorkspaceContext(
            new FakeProvider(),
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            secrets);
        ctx.Project = new Project { Directory = Path.Combine(_root, Guid.NewGuid().ToString("N")), Manifest = manifest };
        var vm = new ConnectionsViewModel(ctx);
        vm.RefreshConnections();
        return (ctx, vm, secrets);
    }

    // ---- round trip ----------------------------------------------------------------------------------

    [Fact]
    public void Every_field_but_the_id_and_exposure_survives_export_and_import()
    {
        var source = Full();
        var text = ConnectionExportFile.Write(
            ConnectionExportFile.Select(Manifest([source])), DateTimeOffset.UnixEpoch);

        var read = ConnectionExportFile.Read(text);

        Assert.True(read.Ok, read.Refusal);
        var got = Assert.Single(read.Connections);
        // By reflection, so a field added to ConnectionInfo later fails here until the file carries it.
        var carried = typeof(ConnectionInfo).GetProperties()
            .Where(p => p.Name is not nameof(ConnectionInfo.Id) and not nameof(ConnectionInfo.ExternalAccess))
            .ToList();
        Assert.NotEmpty(carried);
        foreach (var property in carried)
        {
            if (property.Name == nameof(ConnectionInfo.Options))
            {
                Assert.Equal("bearing-test", got.Options["ApplicationName"]);
                continue;
            }
            Assert.Equal(property.GetValue(source), property.GetValue(got));
        }
    }

    [Fact]
    public void The_safety_settings_arrive_as_they_left()
    {
        // Named on their own because losing one of these is the worst thing a move to a new machine could do.
        var read = ConnectionExportFile.Read(ConnectionExportFile.Write(
            ConnectionExportFile.Select(Manifest([Full()])), DateTimeOffset.UnixEpoch));

        var got = read.Connections.Single();
        Assert.Equal(TlsMode.VerifyFull, got.Tls);
        Assert.True(got.ReadOnly);
        Assert.Equal(30, got.StatementTimeoutSeconds);
        Assert.True(got.ManualCommit);
        Assert.True(got.RequireWriteConfirmation);
        Assert.False(got.ConfirmFullScans);
        Assert.Equal("UTC", got.SessionTimeZone);
        Assert.Equal("bastion.example", got.SshTunnel!.Host);
        Assert.Equal("SHA256:pinned", got.SshTunnel.HostKeyFingerprint);
    }

    [Fact]
    public void An_imported_connection_gets_a_fresh_id_and_no_exposure()
    {
        var source = Full();
        var read = ConnectionExportFile.Read(ConnectionExportFile.Write(
            ConnectionExportFile.Select(Manifest([source])), DateTimeOffset.UnixEpoch));

        var got = read.Connections.Single();
        Assert.NotEqual(source.Id, got.Id);
        Assert.NotEqual(Guid.Empty, got.Id);
        Assert.Equal(ExternalAccess.None, got.ExternalAccess);
    }

    [Fact]
    public void Folders_travel_including_an_empty_one()
    {
        var manifest = Manifest(
            [Plain("a", "Clients/Acme"), Plain("b", "Clients/Globex/Prod"), Plain("c")],
            "Clients", "Clients/Acme", "Archive/Empty");

        var read = ConnectionExportFile.Read(
            ConnectionExportFile.Write(ConnectionExportFile.Select(manifest), DateTimeOffset.UnixEpoch));

        Assert.True(read.Ok, read.Refusal);
        Assert.Contains("Archive/Empty", read.Folders);
        Assert.Contains("Clients/Globex/Prod", read.Folders);
        Assert.Equal("Clients/Acme", read.Connections.Single(c => c.Name == "a").Folder);
        Assert.Null(read.Connections.Single(c => c.Name == "c").Folder);
    }

    [Fact]
    public async Task Importing_an_export_rebuilds_the_folder_tree_in_another_project()
    {
        var manifest = Manifest(
            [Plain("a", "Clients/Acme", "h1"), Plain("b", "Clients/Globex", "h2")], "Archive/Empty");
        var text = ConnectionExportFile.Write(ConnectionExportFile.Select(manifest), DateTimeOffset.UnixEpoch);

        var (ctx, vm, _) = NewVm(new ProjectManifest());
        var read = ConnectionExportFile.Read(text);
        await vm.ImportConnectionsAsync(read.Connections, ConnectionExportFile.FoldersFor(read, read.Connections));

        var landed = ctx.Project!.Manifest;
        Assert.Equal(["a", "b"], landed.Connections.Select(c => c.Name).OrderBy(n => n));
        Assert.Equal("Clients/Acme", landed.Connections.Single(c => c.Name == "a").Folder);
        Assert.Contains("Archive/Empty", landed.ConnectionFolders);
        Assert.Contains("Clients/Globex", landed.ConnectionFolders);
    }

    [Fact]
    public async Task A_name_already_taken_gets_the_import_suffix()
    {
        var text = ConnectionExportFile.Write(
            ConnectionExportFile.Select(Manifest([Plain("prod", host: "new-host")])), DateTimeOffset.UnixEpoch);
        var (ctx, vm, _) = NewVm(Manifest([Plain("prod", host: "old-host")]));

        var read = ConnectionExportFile.Read(text);
        await vm.ImportConnectionsAsync(read.Connections, ConnectionExportFile.FoldersFor(read, read.Connections));

        Assert.Equal(["prod", "prod (copy)"], ctx.Project!.Manifest.Connections.Select(c => c.Name).OrderBy(n => n));
    }

    [Fact]
    public void Folders_whose_connections_were_all_unticked_are_not_created()
    {
        var manifest = Manifest([Plain("keep", "Keep"), Plain("drop", "Drop")], "Empty");
        var read = ConnectionExportFile.Read(
            ConnectionExportFile.Write(ConnectionExportFile.Select(manifest), DateTimeOffset.UnixEpoch));

        var folders = ConnectionExportFile.FoldersFor(read, read.Connections.Where(c => c.Name == "keep").ToList());

        Assert.Contains("Keep", folders);
        Assert.Contains("Empty", folders);      // declared empty in the file: part of the structure
        Assert.DoesNotContain("Drop", folders); // everything in it was declined
    }

    // ---- what the file must never hold --------------------------------------------------------------

    [Fact]
    public async Task The_written_file_holds_no_password_ssh_secret_id_or_exposure()
    {
        var conn = Full();
        var (_, vm, secrets) = NewVm(Manifest([conn]));
        secrets.Seed(conn.Id, Password);
        secrets.Seed(SshSecretId.For(conn.Id), SshSecret);
        var path = Path.Combine(_root, "export.json");

        Assert.True(await vm.ExportConnectionsAsync(path, vm.ExportSelection()!));

        var bytes = await File.ReadAllBytesAsync(path);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(Password, text);
        Assert.DoesNotContain(SshSecret, text);
        Assert.DoesNotContain(conn.Id.ToString("D"), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(conn.Id.ToString("N"), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExternalAccess", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SshSecretId.For(conn.Id).ToString("D"), text, StringComparison.OrdinalIgnoreCase);
        // And it is still a file we can read back, so the absences above are not an empty file passing.
        Assert.True(ConnectionExportFile.Read(text).Ok);
    }

    [Fact]
    public async Task A_write_that_fails_is_reported_not_thrown()
    {
        var (ctx, vm, _) = NewVm(Manifest([Plain("a")]));
        var status = "";
        ctx.Status = s => status = s;
        var path = Path.Combine(_root, "no-such-dir", "deeper", "export.json");

        Assert.False(await vm.ExportConnectionsAsync(path, vm.ExportSelection()!));
        Assert.StartsWith("Could not write export.json", status);
    }

    [Fact]
    public async Task Exporting_nothing_writes_nothing()
    {
        var (_, vm, _) = NewVm(Manifest([Plain("a", "A")], "Empty"));
        var path = Path.Combine(_root, "empty.json");

        Assert.False(await vm.ExportConnectionsAsync(path, vm.ExportSelection(folder: "Empty")!));
        Assert.False(File.Exists(path));
    }

    // ---- scope ---------------------------------------------------------------------------------------

    [Fact]
    public void A_folder_export_covers_its_subtree_and_nothing_else()
    {
        var manifest = Manifest(
            [Plain("in", "Clients"), Plain("deep", "Clients/Acme/Prod"), Plain("out", "Internal"), Plain("root")],
            "Clients/Empty", "Internal/Other");

        var selection = ConnectionExportFile.Select(manifest, folder: "Clients");

        Assert.Equal(["deep", "in"], selection.Connections.Select(c => c.Name).OrderBy(n => n));
        Assert.Contains("Clients/Empty", selection.Folders);
        Assert.DoesNotContain("Internal/Other", selection.Folders);
    }

    [Fact]
    public void A_connection_export_covers_that_connection_alone()
    {
        var target = Plain("one", "A/B");
        var selection = ConnectionExportFile.Select(Manifest([target, Plain("two", "A/B")]), connectionId: target.Id);

        Assert.Equal("one", Assert.Single(selection.Connections).Name);
        Assert.Equal(["A/B"], selection.Folders);
    }

    // ---- refusing files that aren't ours -------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"bearing.connections-export\"")]
    [InlineData("{\"folders\":{},\"connections\":[{\"name\":\"x\",\"configuration\":{\"host\":\"h\"}}]}")] // DBeaver-like
    [InlineData("{\"Format\":\"someone.else\",\"Version\":1,\"Connections\":{}}")]
    [InlineData("{\"Name\":\"prod\",\"Host\":\"h\"}")]
    public void A_file_that_is_not_ours_is_refused_with_a_sentence(string text)
    {
        var read = ConnectionExportFile.Read(text);

        Assert.False(read.Ok);
        Assert.Empty(read.Connections);
        Assert.False(string.IsNullOrWhiteSpace(read.Refusal));
        Assert.EndsWith(".", read.Refusal);
    }

    [Fact]
    public void A_file_from_a_newer_bearing_says_so()
    {
        var text = ConnectionExportFile.Write(ConnectionExportFile.Select(Manifest([Plain("a")])), DateTimeOffset.UnixEpoch)
            .Replace("\"Version\": 1,", "\"Version\": 7,");

        var read = ConnectionExportFile.Read(text);

        Assert.False(read.Ok);
        Assert.Contains("newer Bearing", read.Refusal);
        Assert.Contains("7", read.Refusal);
    }

    [Fact]
    public void A_damaged_export_is_refused_as_damaged_rather_than_foreign()
    {
        var read = ConnectionExportFile.Read("{\"Format\":\"bearing.connections-export\",\"Version\":1,\"Connections\":{\"Kind\":\"bearing.connections\",\"Version\":1,\"Connections\":\"oops\"}}");

        Assert.False(read.Ok);
        Assert.Contains("damaged", read.Refusal);
    }

    [Fact]
    public void An_export_with_no_connections_says_so()
    {
        var read = ConnectionExportFile.Read("{\"Format\":\"bearing.connections-export\",\"Version\":1,\"Connections\":{\"Kind\":\"bearing.connections\",\"Version\":1,\"Connections\":[]}}");

        Assert.False(read.Ok);
        Assert.Equal("The file holds no connections.", read.Refusal);
    }

    [Fact]
    public void Copied_connection_text_saved_as_a_file_is_ours_too()
    {
        var read = ConnectionExportFile.Read(ConnectionClipboard.Write([Plain("pasted", "Here")]));

        Assert.True(read.Ok, read.Refusal);
        Assert.Equal("pasted", read.Connections.Single().Name);
        Assert.Equal(["Here"], read.Folders);
    }

    // ---- the review --------------------------------------------------------------------------------

    [Fact]
    public void The_review_lists_an_unknown_engine_as_not_imported_and_explains_passwords()
    {
        var unknown = Plain("future") with { ProviderId = "cockroach" };
        var read = ConnectionExportFile.Read(ConnectionExportFile.Write(
            ConnectionExportFile.Select(Manifest([Plain("pg"), unknown])), DateTimeOffset.UnixEpoch));

        var review = ConnectionExportFile.Review(read, id => id == "postgres");

        Assert.Equal("pg", Assert.Single(review.Connections).Name);
        Assert.Equal("future", Assert.Single(review.Skipped).Name);
        Assert.Contains(ConnectionExportFile.SecretsNote, review.Warnings);
        Assert.Contains("asks for its password once", ConnectionExportFile.SecretsNote);
    }
}
