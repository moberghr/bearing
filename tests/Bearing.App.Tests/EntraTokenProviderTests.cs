using System;
using System.Collections.Generic;
using System.Text.Json;
using Bearing.App.Connections;
using Bearing.Core.Data;
using Bearing.Sessions;
using Xunit;

namespace Bearing.App.Tests;

public class EntraTokenProviderTests
{
    [Fact]
    public void Parses_the_epoch_expires_on_field()
    {
        // az newer form: expires_on is unix epoch seconds.
        var json = """{ "accessToken": "abc.def", "expires_on": 1893499200, "tokenType": "Bearer" }""";
        var cred = EntraTokenProvider.ParseTokenResponse(json);

        Assert.Equal("abc.def", cred.Secret);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893499200), cred.ExpiresAt);
    }

    [Fact]
    public void Parses_expires_on_when_emitted_as_a_numeric_string()
    {
        var json = """{ "accessToken": "t", "expires_on": "1893499200" }""";
        var cred = EntraTokenProvider.ParseTokenResponse(json);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893499200), cred.ExpiresAt);
    }

    [Fact]
    public void Falls_back_to_the_local_expiresOn_string_when_no_epoch()
    {
        var json = """{ "accessToken": "t", "expiresOn": "2030-01-02 03:04:05.000000" }""";
        var cred = EntraTokenProvider.ParseTokenResponse(json);

        Assert.NotNull(cred.ExpiresAt);
        Assert.Equal(2030, cred.ExpiresAt!.Value.LocalDateTime.Year);
        Assert.Equal(1, cred.ExpiresAt!.Value.LocalDateTime.Month);
        Assert.Equal(2, cred.ExpiresAt!.Value.LocalDateTime.Day);
    }

    [Fact]
    public void A_token_with_no_expiry_fields_parses_with_a_null_expiry()
    {
        var cred = EntraTokenProvider.ParseTokenResponse("""{ "accessToken": "t" }""");
        Assert.Equal("t", cred.Secret);
        Assert.Null(cred.ExpiresAt);
    }

    [Fact]
    public void Missing_accessToken_throws_a_clear_format_error()
        => Assert.Throws<FormatException>(() => EntraTokenProvider.ParseTokenResponse("""{ "expires_on": 1 }"""));

    [Fact]
    public void Malformed_json_throws()
        => Assert.ThrowsAny<JsonException>(() => EntraTokenProvider.ParseTokenResponse("not json"));

    // ---- Which audience the token is minted for -------------------------------------------------------

    private static ConnectionInfo Target(string providerId, params string[] options)
    {
        var map = new Dictionary<string, string>();
        for (var i = 0; i + 1 < options.Length; i += 2) map[options[i]] = options[i + 1];
        return new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "c",
            ProviderId = providerId,
            Options = map,
        };
    }

    [Fact]
    public void The_resource_is_the_engines_own_audience()
    {
        // A token minted for Azure Database for PostgreSQL is rejected by Azure SQL, so this is not a
        // cosmetic string. The Postgres value is unchanged: an existing Entra connection must keep minting
        // exactly the token it minted before a second engine arrived.
        Assert.Equal("https://ossrdbms-aad.database.windows.net",
            EntraTokenProvider.ResourceFor(Target("postgres")));
        Assert.Equal("https://database.windows.net/",
            EntraTokenProvider.ResourceFor(Target("sqlserver")));
    }

    [Fact]
    public void An_explicit_option_still_overrides_the_engines_audience()
    {
        // Sovereign clouds and preview audiences need no code change.
        Assert.Equal("https://ossrdbms-aad.database.chinacloudapi.cn",
            EntraTokenProvider.ResourceFor(Target("postgres",
                EntraTokenProvider.ResourceOptionKey, " https://ossrdbms-aad.database.chinacloudapi.cn ")));
    }

    // ---- The az-login user -----------------------------------------------------------------------------

    /// <summary>A JWT with <paramref name="claims"/> as its payload. Header and signature are placeholders:
    /// <see cref="EntraTokenProvider.UserFromToken"/> reads the claims and nothing else.</summary>
    private static string Jwt(string claims)
        => "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(claims))
               .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    [Fact]
    public void The_user_is_the_tokens_upn()
        => Assert.Equal("ana@contoso.com", EntraTokenProvider.UserFromToken(
            Jwt("""{ "upn": "ana@contoso.com", "preferred_username": "other@contoso.com" }""")));

    [Fact]
    public void A_v2_token_names_the_user_by_preferred_username()
        => Assert.Equal("ana@contoso.com", EntraTokenProvider.UserFromToken(
            Jwt("""{ "preferred_username": "ana@contoso.com" }""")));

    [Fact]
    public void A_payload_needing_padding_and_url_safe_characters_still_decodes()
    {
        // This payload's base64 holds both '/' and '+' (base64url: '_' and '-') and needs one '=' of padding,
        // which a JWT strips.
        var upn = "ana??>>@contoso.com";
        Assert.Equal(upn, EntraTokenProvider.UserFromToken(Jwt($$"""{"upn":"{{upn}}"}""")));
    }

    [Theory]
    [InlineData("""{ "oid": "9b2f…", "appid": "c0ffee" }""")]       // a service principal: no user claim at all
    [InlineData("""{ "unique_name": "live.com#ana@outlook.com" }""")] // a guest's unique_name is no role name
    [InlineData("""{ "upn": "  " }""")]
    public void No_user_principal_name_is_refused_with_what_to_do_instead(string claims)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => EntraTokenProvider.UserFromToken(Jwt(claims)));
        Assert.Contains("type the role name", ex.Message);
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("a.!!!.c")]
    [InlineData("a.bm90IGpzb24.c")]   // "not json"
    public void A_token_that_is_not_a_readable_jwt_is_refused(string token)
        => Assert.Throws<InvalidOperationException>(() => EntraTokenProvider.UserFromToken(token));

    [Theory]
    [InlineData("postgres", CredentialKind.EntraToken, true, true)]
    [InlineData("postgres", CredentialKind.EntraToken, false, false)]
    [InlineData("postgres", CredentialKind.StoredPassword, true, false)]   // a tick left on another kind
    [InlineData("sqlserver", CredentialKind.EntraToken, true, false)]      // the token is the identity there
    public void Az_is_asked_for_the_user_only_where_it_is_used(
        string providerId, CredentialKind kind, bool flag, bool wanted)
        => Assert.Equal(wanted, EntraTokenProvider.WantsUser(Target(providerId) with
        {
            CredentialKind = kind,
            UserFromEntraLogin = flag,
        }));

    [Fact]
    public void A_resolved_user_replaces_the_stored_one_and_no_user_leaves_the_connection_alone()
    {
        var info = Target("postgres") with
        {
            User = "",
            CredentialKind = CredentialKind.EntraToken,
            UserFromEntraLogin = true,
        };

        Assert.Equal("ana@contoso.com", new Credential("tok", null, "ana@contoso.com").ApplyTo(info).User);
        Assert.Same(info, new Credential("tok", null).ApplyTo(info));
    }

    [Theory]
    [InlineData("postgres", CredentialKind.EntraToken, false)]  // unticked after the credential was cached
    [InlineData("postgres", CredentialKind.StoredPassword, true)]
    [InlineData("sqlserver", CredentialKind.EntraToken, true)]
    public void A_resolved_user_is_ignored_where_the_connection_does_not_ask_for_it(
        string providerId, CredentialKind kind, bool flag)
    {
        var info = Target(providerId) with { User = "reporting_ro", CredentialKind = kind, UserFromEntraLogin = flag };

        Assert.Same(info, new Credential("tok", null, "ana@contoso.com").ApplyTo(info));
    }

    [Fact]
    public void A_blank_override_falls_back_rather_than_minting_for_nothing()
        => Assert.Equal("https://database.windows.net/",
            EntraTokenProvider.ResourceFor(Target("sqlserver", EntraTokenProvider.ResourceOptionKey, "   ")));

    // #167: launched from Finder/Spotlight/dock, PATH is launchd's minimal one and misses Homebrew.
    [Fact]
    public void Finds_homebrew_az_when_launchd_path_misses_it()
        => Assert.Equal("/opt/homebrew/bin/az",
            EntraTokenProvider.ResolveAz("/usr/bin:/bin:/usr/sbin:/sbin", p => p == "/opt/homebrew/bin/az", isWindows: false));

    [Fact]
    public void Prefers_az_on_path_over_the_well_known_dirs()
        => Assert.Equal("/home/me/bin/az",
            EntraTokenProvider.ResolveAz("/home/me/bin:/usr/bin", p => p is "/home/me/bin/az" or "/opt/homebrew/bin/az", isWindows: false));

    [Fact]
    public void Falls_back_to_bare_az_when_nothing_is_found()
        => Assert.Equal("az", EntraTokenProvider.ResolveAz(null, _ => false, isWindows: false));

    [Fact]
    public void Windows_keeps_its_own_lookup()
        => Assert.Equal("az", EntraTokenProvider.ResolveAz("/opt/homebrew/bin", _ => true, isWindows: true));
}
