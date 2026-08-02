using System;
using System.Text.Json;
using Squirrel.App.Connections;
using Xunit;

namespace Squirrel.App.Tests;

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
}
