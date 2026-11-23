using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>How a typed domain login is split for NTLM (<see cref="CredentialKind.DomainPassword"/>).</summary>
public class DomainUserTests
{
    [Theory]
    [InlineData(@"mp\ana", "mp", "ana")]
    [InlineData(@"  MP\ana.b  ", "MP", "ana.b")]            // trimmed, case kept
    [InlineData(@"corp.example\svc_app", "corp.example", "svc_app")]
    public void A_down_level_name_is_split_at_the_backslash(string text, string domain, string user)
    {
        Assert.True(DomainUser.TryParse(text, out var login));
        Assert.Equal(new DomainUser(domain, user), login);
    }

    /// <summary>Not split: the part before the @ is not promised to be the account name, and the domain
    /// controller resolves a UPN itself.</summary>
    [Theory]
    [InlineData("ana@example.org")]
    [InlineData("ana.b@corp.example.org")]
    public void A_principal_name_is_passed_whole_with_no_domain(string upn)
    {
        Assert.True(DomainUser.TryParse(upn, out var login));
        Assert.Equal(new DomainUser("", upn), login);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ana")]            // no domain: would log in against whatever the server defaults to
    [InlineData(@"\ana")]
    [InlineData(@"mp\")]
    [InlineData(@"mp\a\b")]
    [InlineData("@example.org")]
    [InlineData("ana@")]
    public void Anything_else_is_refused_rather_than_guessed(string? text)
        => Assert.False(DomainUser.TryParse(text, out _));
}
