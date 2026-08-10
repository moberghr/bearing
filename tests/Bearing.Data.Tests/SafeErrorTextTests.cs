using Bearing.Core.Data;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// What of a driver exception message reaches the user. Credentials are redacted; the endpoint is kept on
/// purpose (the connect path already names it, and it's the useful half of a network error).
/// </summary>
public class SafeErrorTextTests
{
    [Theory]
    [InlineData("Host=db;Port=5432;Password=hunter2;Database=app", "Password=***")]
    [InlineData("failed: password=hunter2", "password=***")]
    [InlineData("pwd = hunter2; Timeout=15", "pwd=***")]
    [InlineData("PASSWD=hunter2", "PASSWD=***")]
    public void Credentials_are_redacted(string message, string expectedFragment)
    {
        var safe = SafeErrorText.Redact(message);

        Assert.Contains(expectedFragment, safe);
        Assert.DoesNotContain("hunter2", safe);
    }

    [Fact]
    public void Redaction_stops_at_the_next_setting_so_the_rest_of_the_message_survives()
    {
        var safe = SafeErrorText.Redact("Host=db.internal;Password=hunter2;Database=app");

        Assert.Contains("Host=db.internal", safe);   // endpoint kept — deliberately
        Assert.Contains("Database=app", safe);
        Assert.DoesNotContain("hunter2", safe);
    }

    [Fact]
    public void A_message_with_no_credentials_is_untouched()
    {
        const string message = "Failed to connect to 10.0.0.5:5432 (connection refused)";
        Assert.Equal(message, SafeErrorText.Redact(message));
    }

    [Fact]
    public void An_exception_with_no_message_falls_back_to_its_type_name()
    {
        var ex = new EmptyMessageException();
        Assert.Equal(nameof(EmptyMessageException), SafeErrorText.Of(ex));
    }

    private sealed class EmptyMessageException : Exception
    {
        public EmptyMessageException() : base("") { }
    }
}
