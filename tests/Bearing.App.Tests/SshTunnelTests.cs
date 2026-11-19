using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Sessions;
using Xunit;

namespace Bearing.App.Tests;

public class SshTunnelTests
{
    private static readonly SshTunnel Pinned = new()
    {
        Host = "bastion",
        Port = 22,
        User = "jump",
        HostKeyFingerprint = "SHA256:abc",
    };

    [Fact]
    public void The_ssh_secret_id_is_stable_and_never_the_connection_id()
    {
        var id = Guid.NewGuid();
        Assert.Equal(SshSecretId.For(id), SshSecretId.For(id));
        Assert.NotEqual(id, SshSecretId.For(id));
        Assert.NotEqual(SshSecretId.For(id), SshSecretId.For(Guid.NewGuid()));
    }

    [Fact]
    public void Pinning_a_host_key_is_not_a_route_change()
    {
        Assert.True(SshTunnel.SameRoute(Pinned, Pinned with { HostKeyFingerprint = null }));
        Assert.True(SshTunnel.SameRoute(null, null));
        Assert.False(SshTunnel.SameRoute(Pinned, null));
        Assert.False(SshTunnel.SameRoute(Pinned, Pinned with { Port = 2222 }));
        Assert.False(SshTunnel.SameRoute(Pinned, Pinned with { PrivateKeyPath = "~/.ssh/id_rsa" }));
    }

    [Fact]
    public void An_unticked_tab_is_a_direct_connection()
        => Assert.Null(SshTunnelForm.Build(false, "bastion", "22", "jump", SshAuthKind.Password, null, Pinned, forgetHostKey: false));

    [Fact]
    public void The_form_keeps_the_pin_for_the_same_server_and_drops_it_otherwise()
    {
        var same = SshTunnelForm.Build(true, " bastion ", "", "jump", SshAuthKind.PrivateKey, " ", Pinned, forgetHostKey: false)!;
        Assert.Equal("SHA256:abc", same.HostKeyFingerprint);
        Assert.Equal(22, same.Port);
        Assert.Null(same.PrivateKeyPath);
        Assert.Equal("bastion", same.Host);

        Assert.Null(SshTunnelForm.Build(true, "other", "22", "jump", SshAuthKind.Password, null, Pinned, false)!.HostKeyFingerprint);
        Assert.Null(SshTunnelForm.Build(true, "bastion", "2222", "jump", SshAuthKind.Password, null, Pinned, false)!.HostKeyFingerprint);
        Assert.Null(SshTunnelForm.Build(true, "bastion", "22", "jump", SshAuthKind.Password, null, Pinned, forgetHostKey: true)!.HostKeyFingerprint);
    }

    [Fact]
    public void Save_keeps_a_pin_that_landed_while_the_dialog_was_open()
    {
        // The dialog opened unpinned; its Test (or a background connect) pinned the saved record meanwhile.
        var fromDialog = Pinned with { HostKeyFingerprint = null };

        Assert.Equal("SHA256:abc", SshTunnelForm.WithSavedPin(fromDialog, Pinned, forgetHostKey: false)!.HostKeyFingerprint);

        // Forget host key, another server, or a pin of its own: the incoming tunnel stands.
        Assert.Null(SshTunnelForm.WithSavedPin(fromDialog, Pinned, forgetHostKey: true)!.HostKeyFingerprint);
        Assert.Null(SshTunnelForm.WithSavedPin(fromDialog with { Host = "other" }, Pinned, false)!.HostKeyFingerprint);
        Assert.Null(SshTunnelForm.WithSavedPin(fromDialog with { Port = 2222 }, Pinned, false)!.HostKeyFingerprint);
        Assert.Equal("SHA256:own", SshTunnelForm.WithSavedPin(Pinned with { HostKeyFingerprint = "SHA256:own" }, Pinned, false)!.HostKeyFingerprint);
        Assert.Null(SshTunnelForm.WithSavedPin(null, Pinned, false));
        Assert.Null(SshTunnelForm.WithSavedPin(fromDialog, null, false)!.HostKeyFingerprint);
    }

    [Theory]
    [InlineData("", 22)]
    [InlineData("abc", 22)]
    [InlineData("0", 22)]
    [InlineData("70000", 22)]
    [InlineData("2222", 2222)]
    public void An_unreadable_port_is_22(string typed, int expected)
        => Assert.Equal(expected, SshTunnelForm.Build(true, "h", typed, "u", SshAuthKind.Password, null, null, false)!.Port);

    [Fact]
    public void Password_and_key_are_separate_logins_with_their_own_secret()
    {
        var password = SshTunnelForm.Build(true, "h", "", "u", SshAuthKind.Password, "~/.ssh/id_rsa", null, false)!;
        Assert.False(password.UsesPrivateKey);
        Assert.Null(password.PrivateKeyPath);   // a key path left in the hidden box is not a key login
        Assert.Equal("pw", SshTunnelForm.SecretToStore(password, "pw", "phrase"));

        var key = SshTunnelForm.Build(true, "h", "", "u", SshAuthKind.PrivateKey, "", null, false)!;
        Assert.True(key.UsesPrivateKey);
        Assert.Null(key.PrivateKeyPath);        // empty = ssh's default keys
        Assert.Equal("phrase", SshTunnelForm.SecretToStore(key, "pw", "phrase"));
        Assert.Equal("", SshTunnelForm.SecretToStore(key, "pw", null));   // unencrypted key: nothing stored

        Assert.Equal("", SshTunnelForm.SecretToStore(null, "pw", "phrase"));  // no tunnel: delete any secret
    }

    [Fact]
    public void Save_leaves_the_keychain_alone_when_nothing_changed()
    {
        var tunnel = SshTunnelForm.Build(true, "h", "", "u", SshAuthKind.Password, null, null, false)!;

        Assert.Null(SshTunnelForm.SecretToSave(null, null, null, "pw", "phrase"));          // never had a tunnel
        Assert.Null(SshTunnelForm.SecretToSave(tunnel, tunnel, "pw", "pw", null));          // same secret as opened
        Assert.Null(SshTunnelForm.SecretToSave(tunnel, tunnel, null, "", null));            // still nothing stored
        Assert.Equal("new", SshTunnelForm.SecretToSave(tunnel, tunnel, "pw", "new", null)); // changed
        Assert.Equal("", SshTunnelForm.SecretToSave(null, tunnel, "pw", "pw", null));       // tunnel removed: delete
    }

    [Fact]
    public void The_form_names_what_a_ticked_tab_is_missing()
    {
        Assert.Null(SshTunnelForm.Problem(null));
        Assert.NotNull(SshTunnelForm.Problem(new SshTunnel { Host = "", User = "u" }));
        Assert.NotNull(SshTunnelForm.Problem(new SshTunnel { Host = "h", User = " " }));
        Assert.Null(SshTunnelForm.Problem(new SshTunnel { Host = "h", User = "u" }));
        Assert.Null(SshTunnelForm.Problem(new SshTunnel { Host = "h", User = "u" }, "db01"));
        Assert.Contains("named instance", SshTunnelForm.Problem(new SshTunnel { Host = "h", User = "u" }, @"db01\SQLEXPRESS"));
        Assert.Null(SshTunnelForm.Problem(null, @"db01\SQLEXPRESS"));   // direct: a named instance is fine
    }

    [Fact]
    public async Task A_direct_connection_routes_to_itself_without_opening_anything()
    {
        var info = new ConnectionInfo { Id = Guid.NewGuid(), Name = "x", ProviderId = "mysql", Host = "db", Port = 3306 };
        var (target, tunnel) = await ConnectionFactoryBuilder.RouteAsync(null, new ProviderRegistry().Get("mysql"), info, default);
        Assert.Same(info, target);
        Assert.Null(tunnel);
    }

    /// <summary>DynamoDB has a region and an HTTPS endpoint, not a host and port to forward: a tunnel a hand-edited
    /// project names is refused before anything is opened, never routed to "127.0.0.1" as a region.</summary>
    [Fact]
    public async Task A_tunnel_on_an_engine_that_cannot_be_tunnelled_is_refused_before_anything_opens()
    {
        var info = new ConnectionInfo
        {
            Id = Guid.NewGuid(), Name = "orders", ProviderId = "dynamodb", Host = "eu-west-1",
            CredentialKind = CredentialKind.AwsProfile, SshTunnel = new SshTunnel { Host = "bastion", User = "u" },
        };
        // No resolver: the refusal comes before anything that could open a tunnel is even needed.
        var ex = await Assert.ThrowsAsync<ConnectionFailedException>(
            () => ConnectionFactoryBuilder.RouteAsync(null, new ProviderRegistry().Get("dynamodb"), info, default));

        Assert.Contains("'orders' names an SSH tunnel", ex.Message);
        Assert.Contains("Amazon DynamoDB", ex.Message);
    }

    [Fact]
    public async Task A_missing_key_file_fails_as_a_connection_error_naming_the_file()
    {
        // Before any socket is opened: the key path is checked first, and the failure is a
        // ConnectionFailedException the connect path reports, never a raw IO exception.
        var tunnels = new SshTunnels(() => null, prompt: null);
        var info = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "x",
            ProviderId = "mysql",
            Host = "db",
            Port = 3306,
            SshTunnel = new SshTunnel { Host = "127.0.0.1", Port = 1, User = "u", Auth = SshAuthKind.PrivateKey, PrivateKeyPath = "/nonexistent/key" },
        };
        var ex = await Assert.ThrowsAsync<ConnectionFailedException>(() => tunnels.OpenAsync(info, info.Host, info.Port, default));
        Assert.Contains("SSH key file not found", ex.Message);
    }

    [Theory]
    [InlineData("sqlserver", 0, 1433)]       // SQL Server's "default port" is a real one through a tunnel
    [InlineData("sqlserver", 14330, 14330)]
    [InlineData("mysql", 0, 3306)]
    [InlineData("postgres", 6543, 6543)]
    public void The_forward_target_is_the_database_as_the_ssh_server_sees_it(string providerId, int port, int expected)
    {
        var provider = new ProviderRegistry().Get(providerId);
        var info = new ConnectionInfo { Id = Guid.NewGuid(), Name = "x", ProviderId = providerId, Host = " db01 ", Port = port };
        Assert.Equal(("db01", expected), ConnectionFactoryBuilder.ForwardTarget(provider, info));
    }

    [Fact]
    public void A_named_instance_is_refused_before_any_tunnel_opens()
    {
        var provider = new ProviderRegistry().Get("sqlserver");
        var info = new ConnectionInfo { Id = Guid.NewGuid(), Name = "x", ProviderId = "sqlserver", Host = @"db01\SQLEXPRESS", Port = 0 };
        var ex = Assert.Throws<ConnectionFailedException>(() => ConnectionFactoryBuilder.ForwardTarget(provider, info));
        Assert.Contains("SQL Server instance", ex.Message);
    }
}
