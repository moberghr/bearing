using System.Net;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Bearing.Core.Data;
using Bearing.Data.DynamoDb;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// What a failed AWS call becomes: its verdict on the neutral scale, and the sentence the user reads. Pure —
/// the exceptions are built from the SDK's real types carrying the real messages (read out of AWSSDK.Core
/// 4.1 and, for the two sign-in cases that matter, produced by it in <c>DynamoDbConnectionFactoryTests</c>),
/// so no credential, network or table is involved.
/// </summary>
public class DynamoDbErrorTextTests
{
    // The key material that must never reach a message. None of it is real; each has the shape of the thing.
    private const string FakeKeyId = "AKIAFAKEKEYID1234567";
    private const string FakeSecret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYFAKESECRET";
    private const string FakeToken = "IQoJb3JpZ2luX2VjFAKESESSIONTOKEN";

    private static string Leaky(string message)
        => $"{message} [AccessKeyId={FakeKeyId} aws_secret_access_key={FakeSecret} session_token={FakeToken}]";

    private static AmazonServiceException Service(string code, string message)
        => new(message, ErrorType.Sender, code, "req-1", HttpStatusCode.BadRequest);

    // ---- SC5: sign-in failures --------------------------------------------------------------------

    public static TheoryData<Exception> SignInFailures() => new()
    {
        // The default chain finding nothing (text as AWSSDK.Core 4.1 produces it).
        new AmazonClientException(Leaky(
            "Failed to resolve AWS credentials. The credential providers used to search for credentials returned the following errors:\n\n"
            + "Exception 1 of 4: The environment variables AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY/AWS_SESSION_TOKEN were not set with AWS credentials.")),
        // A signed-out or never-signed-in SSO profile.
        new AmazonClientException(Leaky("No valid SSO Token could be found.")),
        new AmazonClientException(Leaky("SSO Token has expired and can not be refreshed")),
        new AmazonClientException(Leaky("Unable to get credentials from SSO")),
        // An unknown profile, typed.
        new ProfileNotFoundException(Leaky("Unable to find a profile named 'dev'")),
        // A role profile whose source credentials could not be produced: the SDK wraps the cause.
        new AmazonClientException(
            "Error calling AssumeRole for role arn:aws:iam::123456789012:role/x",
            new AmazonClientException(Leaky("Failed to resolve AWS credentials."))),
        // DynamoDB refusing the credentials it was given.
        Service("ExpiredTokenException", Leaky("The security token included in the request is expired")),
        Service("UnrecognizedClientException", Leaky("The security token included in the request is invalid.")),
        // Wrapped by a task and by our own layers.
        new AggregateException(new InvalidOperationException("outer", new AmazonClientException(Leaky("No valid SSO Token could be found.")))),
    };

    [Theory]
    [MemberData(nameof(SignInFailures))]
    public void A_credential_failure_is_authentication_and_names_the_profile_and_the_command(Exception ex)
    {
        Assert.Equal(DbErrorKind.Authentication, DynamoDbErrors.Classify(ex));

        var error = DynamoDbErrors.ToQueryError(ex, "dev", timeoutSeconds: 30);

        Assert.Equal(DbErrorKind.Authentication, DynamoDbErrors.ClassifyState(error.SqlState));
        Assert.Contains("'dev'", error.Message);
        Assert.Contains("aws sso login --profile dev", error.Message);
    }

    [Theory]
    [MemberData(nameof(SignInFailures))]
    public void A_sign_in_sentence_carries_no_key_secret_or_token_text(Exception ex)
    {
        foreach (var profile in new[] { "dev", null })
        {
            var shown = DynamoDbErrors.ToQueryError(ex, profile, timeoutSeconds: 0).Message;
            var thrown = DynamoDbErrors.Translate(ex, profile).Message;

            foreach (var text in new[] { shown, thrown })
            {
                Assert.DoesNotContain(FakeKeyId, text);
                Assert.DoesNotContain(FakeSecret, text);
                Assert.DoesNotContain(FakeToken, text);
                Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY", text);
            }
        }
    }

    [Fact]
    public void A_blank_profile_says_default_credential_chain_and_the_bare_login_command()
    {
        var ex = new AmazonClientException("Failed to resolve AWS credentials.");

        foreach (var blank in new[] { null, "", "   " })
        {
            var message = DynamoDbErrors.ToQueryError(ex, blank, timeoutSeconds: 0).Message;

            Assert.Contains("default credential chain", message);
            Assert.Contains("aws sso login", message);
            Assert.DoesNotContain("--profile", message);
        }
    }

    [Fact]
    public void A_profile_the_sdk_cannot_find_says_so_and_still_names_the_command()
    {
        var ex = new ProfileNotFoundException("Unable to find a profile named 'dev'");

        var message = DynamoDbErrors.ToQueryError(ex, "dev", timeoutSeconds: 0).Message;

        Assert.Contains("'dev' was not found", message);
        Assert.Contains("aws sso login --profile dev", message);
    }

    [Fact]
    public void A_profile_that_exists_is_not_said_to_be_missing()
    {
        var ex = new AmazonClientException("No valid SSO Token could be found.");

        var message = DynamoDbErrors.ToQueryError(ex, "dev", timeoutSeconds: 0).Message;

        Assert.DoesNotContain("was not found", message);
    }

    [Fact]
    public void Translate_wraps_a_sign_in_failure_once_and_leaves_everything_else_alone()
    {
        var sdk = new AmazonClientException("No valid SSO Token could be found.");
        var translated = DynamoDbErrors.Translate(sdk, "dev");

        var wrapped = Assert.IsType<DynamoDbSignInException>(translated);
        Assert.Same(sdk, wrapped.InnerException);
        Assert.Same(wrapped, DynamoDbErrors.Translate(wrapped, "dev"));

        var other = new InvalidOperationException("boom");
        Assert.Same(other, DynamoDbErrors.Translate(other, "dev"));
    }

    // ---- Access denied is a privilege, not a sign-in ----------------------------------------------

    [Fact]
    public void Access_denied_is_unknown_and_carries_the_service_message()
    {
        const string text = "User: arn:aws:sts::123456789012:assumed-role/Dev/me is not authorized to perform: "
            + "dynamodb:PartiQLSelect on resource: arn:aws:dynamodb:eu-west-1:123456789012:table/Orders";
        var ex = Service("AccessDeniedException", text);

        Assert.Equal(DbErrorKind.Unknown, DynamoDbErrors.Classify(ex));

        var error = DynamoDbErrors.ToQueryError(ex, "dev", timeoutSeconds: 0);
        Assert.Equal(DbErrorKind.Unknown, DynamoDbErrors.ClassifyState(error.SqlState));
        Assert.Equal("AccessDeniedException", error.SqlState);
        Assert.Equal(text, error.Message);
    }

    [Fact]
    public void A_service_message_has_key_material_scrubbed_before_it_is_shown()
    {
        var ex = Service("AccessDeniedException", Leaky("User is not authorized"));

        var message = DynamoDbErrors.ToQueryError(ex, "dev", timeoutSeconds: 0).Message;

        Assert.Contains("User is not authorized", message);
        Assert.DoesNotContain(FakeKeyId, message);
        Assert.DoesNotContain(FakeSecret, message);
        Assert.DoesNotContain(FakeToken, message);
    }

    [Fact]
    public void A_service_error_that_merely_mentions_credentials_is_not_a_sign_in_failure()
    {
        // Matching is by exception type: the text of a service error is the user's statement echoed back.
        var ex = Service("ValidationException", "Statement wasn't well formed: select * from \"No valid SSO Token\"");

        Assert.Equal(DbErrorKind.Unknown, DynamoDbErrors.Classify(ex));
        Assert.Equal("ValidationException", DynamoDbErrors.ToQueryError(ex, "dev", timeoutSeconds: 0).SqlState);
    }

    // ---- Our timeout and the user's cancel --------------------------------------------------------

    [Fact]
    public void Our_timeout_is_canceled_and_names_the_connections_statement_timeout()
    {
        var error = DynamoDbErrors.ToQueryError(new OperationCanceledException(), "dev", timeoutSeconds: 45, timedOut: true);

        Assert.Equal(DbErrorKind.Canceled, DynamoDbErrors.ClassifyState(error.SqlState));
        Assert.Contains("statement timeout", error.Message);
        Assert.Contains("45 s", error.Message);
    }

    [Fact]
    public void A_cancel_is_canceled_whether_it_arrives_as_an_error_or_an_exception()
    {
        var ex = new TaskCanceledException();

        Assert.Equal(DbErrorKind.Canceled, DynamoDbErrors.Classify(ex));
        Assert.Equal(
            DbErrorKind.Canceled,
            DynamoDbErrors.ClassifyState(DynamoDbErrors.ToQueryError(ex, null, timeoutSeconds: 0).SqlState));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ValidationException")]
    [InlineData("AccessDeniedException")]
    [InlineData("28P01")]
    public void Nothing_else_is_placed_on_the_scale(string? state)
        => Assert.Equal(DbErrorKind.Unknown, DynamoDbErrors.ClassifyState(state));

    [Fact]
    public void A_refused_setting_is_reported_as_is()
    {
        var ex = new DynamoDbConfigurationException("The AWS region is empty.");

        Assert.Equal(DbErrorKind.Unknown, DynamoDbErrors.Classify(ex));
        Assert.Equal("The AWS region is empty.", ex.Message);
    }

    [Fact]
    public void An_exception_with_no_message_still_says_something()
    {
        var error = DynamoDbErrors.ToQueryError(new BlankException(), null, timeoutSeconds: 0);

        Assert.Equal(nameof(BlankException), error.Message);
    }

    private sealed class BlankException : Exception
    {
        public BlankException() : base("")
        {
        }
    }
}
