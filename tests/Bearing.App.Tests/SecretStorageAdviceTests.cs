using Bearing.App.Connections;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The no-keychain warning has to describe what happened, not what we assume happened. The app once spent a
/// debugging session telling a user "No system keyring found" on a box whose libsecret worked perfectly — the
/// probe had simply run before the keyring was serving. The probe now reports a reason; this is the mapping
/// from that reason to what the connection editor says, including the case where we don't recognise it.
/// </summary>
public class SecretStorageAdviceTests
{
    [Theory]
    [InlineData("no OS credential store is implemented for this platform.", SecretStorageCause.NoPlatformStore)]
    [InlineData("secret-tool: Operation was cancelled", SecretStorageCause.Locked)]
    [InlineData("Prompt dismissed by the user", SecretStorageCause.Locked)]
    [InlineData("The collection is locked", SecretStorageCause.Locked)]
    [InlineData("secret-tool: command not found", SecretStorageCause.HelperMissing)]
    [InlineData("No such file or directory: /usr/bin/secret-tool", SecretStorageCause.HelperMissing)]
    [InlineData("it read the probe secret back as a different value.", SecretStorageCause.Unknown)]
    [InlineData(null, SecretStorageCause.Unknown)]
    public void Classifies_what_the_store_reported(string? reason, SecretStorageCause expected)
        => Assert.Equal(expected, SecretStorageAdvice.Classify(reason));

    [Fact]
    public void An_unrecognised_reason_is_quoted_rather_than_explained_away()
    {
        var why = SecretStorageAdvice.Explanation("it accepted the probe secret and then read it back as missing.");

        Assert.NotNull(why);
        Assert.Contains("read it back as missing", why);
        // No invented diagnosis, and above all not the old claim that no keyring was found.
        Assert.DoesNotContain("not installed", why);
        Assert.DoesNotContain("found", why);
    }

    [Fact]
    public void A_locked_keyring_gets_the_one_hint_worth_giving()
    {
        var why = SecretStorageAdvice.Explanation("secret-tool: the prompt was dismissed");

        Assert.Contains("Unlock", why);
        Assert.Contains("reopen", why);   // reopening the dialog re-probes, which is what makes the hint true
    }

    [Fact]
    public void With_no_reason_at_all_the_warning_says_only_what_it_knows()
    {
        Assert.Null(SecretStorageAdvice.Explanation(null));

        var warning = SecretStorageAdvice.NoStorageWarning(null);
        Assert.Contains("will NOT be saved", warning);
        Assert.Contains("Prompt each time", warning);
        // "Couldn't be reached", never "not found": the probe cannot tell those apart.
        Assert.Contains("could be reached", warning);
        Assert.DoesNotContain("keyring found", warning);
    }

    [Fact]
    public void A_known_cause_is_appended_to_the_warning()
    {
        var warning = SecretStorageAdvice.NoStorageWarning("no OS credential store is implemented for this platform.");

        Assert.Contains("will NOT be saved", warning);
        Assert.Contains("no credential store Bearing can use", warning);
    }
}
