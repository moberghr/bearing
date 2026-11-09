using Bearing.Data.MySql;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>Catalog spellings the two engines disagree on, and the options a shared project file may not set.</summary>
public class MySqlCatalogValuesTests
{
    [Theory]
    [InlineData("NULL", true, null)]          // MariaDB: nullable column with no default
    [InlineData("'none'", true, "none")]      // MariaDB quotes a literal
    [InlineData("'it''s'", true, "it's")]
    [InlineData("'NULL'", true, "NULL")]      // a default that *is* the string "NULL"
    [InlineData(@"'a\\b'", true, @"a\b")]      // MariaDB doubles a backslash (measured on 11.4)
    [InlineData(@"'a\nb'", true, "a\nb")]     // … and writes a newline as \n
    [InlineData("current_timestamp()", true, "current_timestamp()")]
    [InlineData("0", true, "0")]
    [InlineData(null, true, null)]
    [InlineData("none", false, "none")]       // MySQL reports literals bare …
    [InlineData("NULL", false, "NULL")]       // … and never spells "no default" as text
    [InlineData(null, false, null)]
    public void Column_defaults_read_the_same_on_both_engines(string? raw, bool isMariaDb, string? expected)
        => Assert.Equal(expected, MySqlCatalogValues.ColumnDefault(raw, isMariaDb));

    [Fact]
    public void An_unsigned_sequence_bound_is_clamped_and_says_so()
    {
        var max = MySqlCatalogValues.SequenceBound(18446744073709551614UL);
        Assert.Equal(long.MaxValue, max.Value);
        Assert.True(max.Clamped);

        var ordinary = MySqlCatalogValues.SequenceBound(-9223372036854775807L);
        Assert.Equal(-9223372036854775807L, ordinary.Value);
        Assert.False(ordinary.Clamped);

        Assert.Equal(1, MySqlCatalogValues.SequenceBound(1m).Value);
    }

    [Theory]
    [InlineData("CA Certificate File", "/tmp/evil-ca.pem")]
    [InlineData("CACertificateFile", "/tmp/evil-ca.pem")]
    [InlineData("Certificate Thumb Print", "abc")]
    [InlineData("Server Redirection Mode", "Required")]
    [InlineData("ServerRedirectionMode", "Required")]
    [InlineData("SSL CA", "/tmp/evil-ca.pem")]
    [InlineData("Ssl-Ca", "/tmp/evil-ca.pem")]
    [InlineData("Skip Certificate Revocation Check", "true")]
    [InlineData("Allow Load Local Infile", "true")]
    [InlineData("No Backslash Escapes", "true")]
    [InlineData("NoBackslashEscapes", "true")]
    [InlineData("Ignore Prepare", "true")]
    [InlineData("Cancellation Timeout", "-1")]
    [InlineData("CancellationTimeout", "-1")]
    // The builder files this one under its own spelling ("Pipe Name"), which the list missed — so neither the
    // list nor the canonical probe caught it.
    [InlineData("Pipe Name", "evil")]
    public void Transport_and_identity_options_are_reserved_under_every_spelling(string key, string value)
        => Assert.True(MySqlConnectionFactory.IsReserved(key, value));

    [Theory]
    // Each spelling is one the driver accepts and files under the canonical name listed, but is not itself
    // listed — the case a driver upgrade that adds a synonym creates.
    [InlineData("Uid", "evil")]
    [InlineData("Data Source", "evil.example")]
    [InlineData("CA Certificate File", "/tmp/evil-ca.pem")]
    [InlineData("Ssl-Ca", "/tmp/evil-ca.pem")]
    public void A_synonym_missing_from_the_list_is_reserved_by_its_canonical_name(string key, string value)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "User ID", "Server", "SSL CA" };

        Assert.DoesNotContain(key, reserved);
        Assert.True(MySqlConnectionFactory.IsReserved(key, value, reserved));
    }

    [Fact]
    public void The_canonical_probe_does_not_reserve_an_unlisted_setting()
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "User ID" };

        Assert.False(MySqlConnectionFactory.IsReserved("Data Source", "db.example", reserved));
        // A value the builder rejects is left to fail where it is applied, not reserved.
        Assert.False(MySqlConnectionFactory.IsReserved("Port", "not-a-port", reserved));
    }

    [Theory]
    [InlineData("Connection Timeout", "5")]
    [InlineData("Character Set", "utf8mb4")]
    public void Harmless_options_are_not_reserved(string key, string value)
        => Assert.False(MySqlConnectionFactory.IsReserved(key, value));

    [Theory]
    [InlineData("Connection Timeout", true)]
    [InlineData("connect timeout", true)]
    [InlineData("Character Set", true)]
    [InlineData("Connection Lifetime", true)]
    [InlineData("entra.tenant", false)]
    [InlineData("Ssl-Mode", false)]
    public void A_driver_keyword_is_recognised_whether_or_not_it_is_already_set(string key, bool expected)
        // MySqlConnectionStringBuilder.ContainsKey only says whether a key is *set*, which dropped every
        // option the factory had not assigned itself.
        => Assert.Equal(expected, MySqlConnectionFactory.IsDriverKeyword(key));
}
