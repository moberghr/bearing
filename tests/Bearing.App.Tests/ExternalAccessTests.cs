using System;
using System.Linq;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// <see cref="ExternalAccessPolicy"/> — the exposure gate, and the settings an external host is made to
/// connect with whatever the saved record says. Pure, so none of this needs a server or a host.
/// </summary>
public class ExternalAccessTests
{
    private static ConnectionInfo Conn() => new()
    {
        Id = Guid.NewGuid(),
        Name = "reporting",
        ProviderId = "postgres",
        Host = "db.example",
        Database = "app",
        User = "karlo",
    };

    [Fact]
    public void A_connection_says_nothing_about_external_access_until_someone_sets_it()
    {
        // The default matters more than most: a project file written before the setting existed, and a
        // connection nobody thought about, both land here.
        var conn = Conn();

        Assert.Equal(ExternalAccess.None, conn.ExternalAccess);
        Assert.False(ExternalAccessPolicy.IsExposed(conn));
        Assert.Null(ExternalAccessPolicy.ForExternalHost(conn));
    }

    /// <summary>
    /// The point of the forcing: exposing a connection to tooling is not the same decision as making it
    /// read-only for yourself, and a user who had to do both would end up doing neither.
    /// </summary>
    [Fact]
    public void An_exposed_connection_is_opened_read_only_even_where_the_user_writes_to_it()
    {
        var conn = Conn() with { ExternalAccess = ExternalAccess.ReadOnly, ReadOnly = false };

        var external = ExternalAccessPolicy.ForExternalHost(conn);

        Assert.NotNull(external);
        Assert.True(external!.ReadOnly);
        // And the record the user works with is untouched — they can still write to it themselves.
        Assert.False(conn.ReadOnly);
    }

    [Fact]
    public void An_exposed_connection_never_holds_a_transaction_open()
    {
        // Inert while nothing writes, but an external host has no Commit button, no chip and no quit guard,
        // so a transaction opened on one would be exactly the unreachable transaction §1.10 is about.
        var conn = Conn() with { ExternalAccess = ExternalAccess.ReadOnly, ManualCommit = true };

        Assert.False(ExternalAccessPolicy.ForExternalHost(conn)!.ManualCommit);
    }

    [Fact]
    public void A_runaway_query_cannot_outlive_the_caller_when_no_timeout_was_set()
    {
        var conn = Conn() with { ExternalAccess = ExternalAccess.ReadOnly };
        Assert.Equal(SessionPolicy.NoTimeout, conn.StatementTimeoutSeconds);

        Assert.Equal(
            SessionPolicy.PresetTimeoutSeconds,
            ExternalAccessPolicy.ForExternalHost(conn)!.StatementTimeoutSeconds);
    }

    /// <summary>
    /// The fill is a floor, not a ceiling. A connection pointed at slow analytical work has a long timeout
    /// on purpose, and overriding it would make the exposed half of that connection useless for the thing
    /// it exists to do.
    /// </summary>
    [Fact]
    public void A_timeout_the_user_chose_is_kept_however_long_it_is()
    {
        var conn = Conn() with { ExternalAccess = ExternalAccess.ReadOnly, StatementTimeoutSeconds = 3_600 };

        Assert.Equal(3_600, ExternalAccessPolicy.ForExternalHost(conn)!.StatementTimeoutSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_timeout_that_is_not_a_limit_is_filled_in_rather_than_passed_on(int saved)
    {
        // project.json is hand-editable, so a negative value is reachable. It means "no limit" to
        // SessionPolicy, which is the case the fill exists for.
        var conn = Conn() with { ExternalAccess = ExternalAccess.ReadOnly, StatementTimeoutSeconds = saved };

        Assert.Equal(
            SessionPolicy.PresetTimeoutSeconds,
            ExternalAccessPolicy.ForExternalHost(conn)!.StatementTimeoutSeconds);
    }

    [Fact]
    public void A_hand_edited_timeout_past_the_ceiling_is_clamped_before_a_host_sees_it()
    {
        // Unclamped this overflows the conversion to milliseconds negative, which Postgres refuses at
        // startup — turning a silly number in a settings file into a connection that will not open at all.
        var conn = Conn() with { ExternalAccess = ExternalAccess.ReadOnly, StatementTimeoutSeconds = 3_000_000 };

        Assert.Equal(
            SessionPolicy.MaxTimeoutSeconds,
            ExternalAccessPolicy.ForExternalHost(conn)!.StatementTimeoutSeconds);
    }

    [Fact]
    public void The_write_confirmation_setting_is_carried_across_untouched()
    {
        // Nothing can answer it and nothing needs to — the write is already refused. Clearing a safety flag
        // to tidy up a record is how one stops being set when it starts mattering again.
        var conn = Conn() with { ExternalAccess = ExternalAccess.ReadOnly, RequireWriteConfirmation = true };

        Assert.True(ExternalAccessPolicy.ForExternalHost(conn)!.RequireWriteConfirmation);
    }

    /// <summary>
    /// Established by construction, in the shape the clipboard round-trip test uses: the external record
    /// differs from the saved one in exactly three properties and no others. The next field added to
    /// <see cref="ConnectionInfo"/> fails here if this method starts silently rewriting it.
    /// </summary>
    [Fact]
    public void Nothing_but_the_three_forced_settings_differs_from_the_saved_connection()
    {
        // Each of the three is given the value the policy must change, or it would agree by accident.
        var saved = Conn() with
        {
            ExternalAccess = ExternalAccess.ReadOnly,
            ReadOnly = false,
            ManualCommit = true,
            StatementTimeoutSeconds = SessionPolicy.NoTimeout,
        };

        var external = ExternalAccessPolicy.ForExternalHost(saved)!;

        var changed = typeof(ConnectionInfo).GetProperties()
            .Where(p => !Equals(p.GetValue(saved), p.GetValue(external)))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[]
            {
                nameof(ConnectionInfo.ManualCommit),
                nameof(ConnectionInfo.ReadOnly),
                nameof(ConnectionInfo.StatementTimeoutSeconds),
            },
            changed);
    }

    /// <summary>
    /// A kind whose whole mechanism is asking the user cannot work where there is no user — and saying so
    /// up front is better than a host reporting a failed connect for a connection that was never going to
    /// open.
    /// </summary>
    [Fact]
    public void A_connection_that_prompts_for_its_password_cannot_be_opened_without_a_window()
    {
        var conn = Conn() with { ExternalAccess = ExternalAccess.ReadOnly, CredentialKind = CredentialKind.Prompt };

        var reason = ExternalAccessPolicy.UnavailableReason(conn);

        Assert.NotNull(reason);
        Assert.Contains("reporting", reason);
    }

    [Theory]
    [InlineData(CredentialKind.StoredPassword)]
    [InlineData(CredentialKind.Integrated)]
    [InlineData(CredentialKind.EntraToken)]
    public void The_kinds_that_need_no_window_are_not_reported_as_unavailable(CredentialKind kind)
    {
        // Whether the keyring answers, or the az session is still valid, is a runtime fact this must not
        // guess at: reporting it here would assert a cause nobody checked.
        var conn = Conn() with { ExternalAccess = ExternalAccess.ReadOnly, CredentialKind = kind };

        Assert.Null(ExternalAccessPolicy.UnavailableReason(conn));
    }
    /// <summary>
    /// The mark the connection list shows. It is the whole justification for <c>ExternalAccess</c>
    /// travelling in <c>project.json</c> rather than being per-machine — §1.11 argues that opening a shared
    /// project takes its configuration whole, "exposure visible in the connection list with it" — and
    /// nothing rendered it, so opening a colleague's project exposed their marked connections to anything
    /// running as you, findable only by opening each connection's dialog in turn.
    /// </summary>
    [Fact]
    public void An_exposed_connection_says_so_on_its_row()
    {
        var exposed = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "agent-reads",
            ProviderId = "postgres",
            Host = "db.internal",
            Port = 5432,
            ExternalAccess = ExternalAccess.ReadOnly,
        };

        var node = Node(exposed);

        Assert.Contains("bearing", node.Detail);
        Assert.Contains("db.internal", node.Detail);   // still says where the server is
    }

    [Fact]
    public void An_ordinary_connection_carries_no_mark()
    {
        var plain = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "mine",
            ProviderId = "postgres",
            Host = "db.internal",
            Port = 5432,
        };

        Assert.DoesNotContain("bearing", Node(plain).Detail);
    }

    /// <summary>The row is re-labelled when the connection is edited, or unticking the box would appear to
    /// do nothing until the panel was rebuilt — which is the same class of bug as not showing it at all.</summary>
    [Fact]
    public void Withdrawing_exposure_takes_the_mark_off_the_row()
    {
        var exposed = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "agent-reads",
            ProviderId = "postgres",
            Host = "db.internal",
            Port = 5432,
            ExternalAccess = ExternalAccess.ReadOnly,
        };
        var node = Node(exposed);

        node.Adopt(exposed with { ExternalAccess = ExternalAccess.None });

        Assert.DoesNotContain("bearing", node.Detail);
    }

    /// <summary>A server row. The browser is never reached: <c>Detail</c> is composed in the constructor and
    /// only <c>LoadChildrenAsync</c> would ask it anything, so a fifteen-method fake would say nothing these
    /// assertions depend on.</summary>
    private static ServerNodeViewModel Node(ConnectionInfo connection)
        => new(connection, browser: null!);

}
