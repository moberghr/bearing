using Bearing.Persistence;
using Xunit;

namespace Bearing.Persistence.Tests;

/// <summary>
/// <c>security find-generic-password -g</c>'s password line, in the forms measured on macOS. <c>-w</c> was
/// unreadable: it prints a non-ASCII or backslash-bearing password as bare hex, which is indistinguishable
/// from a password that is hex (caught by the macOS CI leg, PlatformKeychainTests).
/// </summary>
public class MacKeychainOutputTests
{
    [Theory]
    [InlineData("password: \"plain pw\"", "plain pw")]
    [InlineData("password: \"deadbeef\"", "deadbeef")]          // hex-looking text stays text
    [InlineData("password: \"0xdead\"", "0xdead")]              // so does a leading 0x, inside quotes
    [InlineData("password: \"a\"b\"", "a\"b")]                  // inner quote is not escaped
    [InlineData("password: \" sp \"", " sp ")]                  // edge spaces kept
    [InlineData("password: ", "")]
    [InlineData("password: \"x\"\r", "x")]
    public void Printable_passwords_are_read_verbatim(string line, string expected)
        => Assert.Equal(expected, MacKeychainSecretStore.ParsePasswordLine(line));

    [Theory]
    [InlineData("password: 0x785C79  \"x\\134y\"", "x\\y")]
    [InlineData("password: 0x70407373202277276F24726422205C20CEA9CEBCCEADCEB3CEB120F09F9490  \"p@ss \"w'o$rd\" \\134 \\316\\251\\316\\274\\316\\255\\316\\263\\316\\261 \\360\\237\\224\\220\"",
        "p@ss \"w'o$rd\" \\ Ωμέγα 🔐")]
    public void Hex_form_is_decoded_as_utf8(string line, string expected)
        => Assert.Equal(expected, MacKeychainSecretStore.ParsePasswordLine(line));

    [Fact]
    public void An_unknown_form_is_an_error_not_a_guess()
        => Assert.Throws<InvalidOperationException>(() => MacKeychainSecretStore.ParsePasswordLine("password: <redacted>"));
}
