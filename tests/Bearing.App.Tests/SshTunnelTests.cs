using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.Core.Data;
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
    public void The_form_names_what_a_ticked_tab_is_missing()
    {
        Assert.Null(SshTunnelForm.Problem(null));
        Assert.NotNull(SshTunnelForm.Problem(new SshTunnel { Host = "", User = "u" }));
        Assert.NotNull(SshTunnelForm.Problem(new SshTunnel { Host = "h", User = " " }));
        Assert.Null(SshTunnelForm.Problem(new SshTunnel { Host = "h", User = "u" }));
    }

    [Fact]
    public async Task A_direct_connection_routes_to_itself_without_opening_anything()
    {
        var info = new ConnectionInfo { Id = Guid.NewGuid(), Name = "x", ProviderId = "mysql", Host = "db", Port = 3306 };
        var (target, tunnel) = await ConnectionFactoryBuilder.RouteAsync(null, info, default);
        Assert.Same(info, target);
        Assert.Null(tunnel);
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
        var ex = await Assert.ThrowsAsync<ConnectionFailedException>(() => tunnels.OpenAsync(info, default));
        Assert.Contains("SSH key file not found", ex.Message);
    }
}
