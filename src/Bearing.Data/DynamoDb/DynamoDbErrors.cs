using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Bearing.Core.Data;

namespace Bearing.Data.DynamoDb;

/// <summary>
/// What went wrong with an AWS call, placed on the neutral <see cref="DbErrorKind"/> scale and said in
/// words the user can act on. Pure, so every verdict and sentence is testable without a credential, a
/// network or a table (§2.5).
/// <para>
/// <b>Sign-in failures name the profile and the command, and nothing the SDK said.</b> The SDK's own text for
/// "no credentials" is a four-part dump of every provider it tried; for an SSO profile it is "No valid SSO
/// Token could be found." Neither tells the user what to run, and neither is ours to vouch for. The sentence
/// here is built from the profile name alone — which is also why no key, secret or token text can reach it:
/// there is no path from an exception's message to a sign-in sentence (§1.1).
/// </para>
/// <para>
/// <b>Everything else the SDK says is scrubbed</b> before it is shown (<see cref="Scrub"/>): an access-key id
/// or a "secret=…" fragment in a service message is redacted, as <see cref="SafeErrorText"/> does for a
/// password in a connection string. Bearing never reads an AWS secret, so this is a second line, not the
/// first.
/// </para>
/// </summary>
internal static class DynamoDbErrors
{
    // QueryError.SqlState for what Bearing itself raised. A service error carries AWS's own error code
    // ("AccessDeniedException", "ValidationException") there instead. These cannot collide with one: AWS
    // codes never start with "Bearing".
    internal const string SignInCode = "BearingSignIn";
    internal const string TimeoutCode = "BearingTimeout";
    internal const string CanceledCode = "BearingCanceled";

    /// <summary>
    /// Error codes DynamoDB answers with when it has been <em>given</em> credentials and refuses them. Not
    /// <c>AccessDeniedException</c>: that one means the credentials are good and the policy says no, which is
    /// a privilege (the MySQL 1044 case) — re-signing in changes nothing, so it stays <see cref="DbErrorKind.Unknown"/>.
    /// </summary>
    private static readonly HashSet<string> RejectedCredentialCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ExpiredToken", "ExpiredTokenException", "UnrecognizedClientException", "InvalidClientTokenId",
    };

    /// <summary>
    /// Fragments of the messages <see cref="AmazonClientException"/> carries when the SDK could not produce
    /// credentials at all — read out of AWSSDK.Core 4.1's resources, and the two that matter exercised
    /// against the real SDK in <c>DynamoDbConnectionFactoryTests</c> (unknown and signed-out SSO profile). A
    /// client-side exception has no error code to branch on, so the text is the only handle there is; it is
    /// matched on <see cref="AmazonClientException"/> alone, never on a service error, so a table name or a
    /// statement that happens to say "credentials" cannot read as a sign-in failure.
    /// </summary>
    private static readonly string[] SignInMessageFragments =
    [
        "No valid SSO Token",
        "SSO Token has expired",
        "Login Token",
        "Unable to get credentials from SSO",
        "Failed to resolve AWS credentials",
        "Credentials must be set",
        "Unable to find credentials",
        "Unable to find a profile named",
        "Unable to find the \"default\" profile",
        "Unable to get credentials for role",
        "Error calling AssumeRole",
        "Unable to get IAM security credentials",
        "Unable to retrieve credentials",
        "retrieved credentials have already expired",
        "Could not resolve identity",
    ];

    /// <summary>Place a failed call on the neutral scale by the code <see cref="ToQueryError"/> recorded.</summary>
    internal static DbErrorKind ClassifyState(string? sqlState) => sqlState switch
    {
        SignInCode => DbErrorKind.Authentication,
        TimeoutCode or CanceledCode => DbErrorKind.Canceled,
        { } code when RejectedCredentialCodes.Contains(code) => DbErrorKind.Authentication,
        _ => DbErrorKind.Unknown,
    };

    /// <summary>The same judgement for a thrown exception — the connect path has no <see cref="QueryError"/>.
    /// Walks the whole chain, aggregates included: the SDK wraps a credential failure in a request failure.</summary>
    internal static DbErrorKind Classify(Exception exception)
        => IsSignInFailure(exception) ? DbErrorKind.Authentication
            : Chain(exception).Any(e => e is OperationCanceledException) ? DbErrorKind.Canceled
            : DbErrorKind.Unknown;

    /// <summary>True when <paramref name="exception"/> or anything beneath it is the SDK failing to sign us
    /// in — credentials it could not find, a profile it could not read, or a token DynamoDB refused.</summary>
    internal static bool IsSignInFailure(Exception exception)
    {
        foreach (var e in Chain(exception))
        {
            switch (e)
            {
                case DynamoDbSignInException:
                case ProfileNotFoundException:
                    return true;
                case AmazonServiceException service when !string.IsNullOrEmpty(service.ErrorCode):
                    if (RejectedCredentialCodes.Contains(service.ErrorCode))
                    {
                        return true;
                    }
                    break;
                case AmazonClientException client:
                    if (SignInMessageFragments.Any(f => client.Message.Contains(f, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                    break;
            }
        }
        return false;
    }

    /// <summary>True when the SDK could not find the named profile at all (as opposed to finding it and
    /// failing to use it) — the sentence differs, since "run aws sso login" is not the first thing to try.</summary>
    internal static bool IsProfileMissing(Exception exception)
    {
        foreach (var e in Chain(exception))
        {
            if (e is ProfileNotFoundException
                || (e is DynamoDbSignInException { ProfileMissing: true })
                || (e is AmazonClientException client
                    && (client.Message.Contains("Unable to find a profile named", StringComparison.OrdinalIgnoreCase)
                        || (client.Message.Contains("Source profile [", StringComparison.OrdinalIgnoreCase)
                            && client.Message.Contains("was not found", StringComparison.OrdinalIgnoreCase)))))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The sentence for a failed sign-in: which profile, and the command that fixes the common case. Bearing
    /// does not launch the command. A blank profile means the SDK's default credential chain, and says so.
    /// </summary>
    internal static string SignInSentence(string? profile, bool profileMissing = false)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return "Could not sign in to AWS with the default credential chain. "
                + "Set an AWS profile on the connection, or run \"aws sso login\" and try again.";
        }

        var name = profile.Trim();
        return profileMissing
            ? $"AWS profile '{name}' was not found. Check its name in your AWS config, "
                + $"or run \"aws sso login --profile {name}\" if it is an SSO profile."
            : $"Could not sign in to AWS with profile '{name}'. If it is an SSO profile, run "
                + $"\"aws sso login --profile {name}\" and try again; otherwise check the profile in your AWS config.";
    }

    /// <summary>
    /// The sentence for a statement stopped by the connection's own clock (<see cref="SessionPolicy"/>,
    /// §1.9a): it names the setting, since the user chose it and may want to raise it. Says nothing about
    /// whether DynamoDB finished the read — a cancelled request is abandoned, not undone.
    /// </summary>
    internal static string TimeoutSentence(int timeoutSeconds)
        => $"Stopped: this connection's statement timeout ({timeoutSeconds} s) elapsed before DynamoDB answered.";

    /// <summary>
    /// A failed call as the <see cref="QueryError"/> the grid shows. <paramref name="timedOut"/> is the
    /// caller's knowledge (its own linked token fired), not something an exception can say.
    /// </summary>
    internal static QueryError ToQueryError(Exception exception, string? profile, int timeoutSeconds, bool timedOut = false)
    {
        if (timedOut)
        {
            return new QueryError(TimeoutSentence(timeoutSeconds), TimeoutCode, null);
        }
        if (IsSignInFailure(exception))
        {
            return new QueryError(SignInSentence(profile, IsProfileMissing(exception)), SignInCode, null);
        }
        if (Chain(exception).Any(e => e is OperationCanceledException))
        {
            return new QueryError("The statement was canceled.", CanceledCode, null);
        }

        var service = Chain(exception).OfType<AmazonServiceException>().FirstOrDefault(s => !string.IsNullOrEmpty(s.ErrorCode));
        var message = Scrub(string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message);
        return new QueryError(message, service?.ErrorCode, null);
    }

    /// <summary>
    /// Turn a sign-in failure into the <see cref="DynamoDbSignInException"/> the connect path shows and
    /// classifies; anything else comes back as it was. Called where an SDK call is first made for a session
    /// (the connection test, the table listing), so the user reads one sentence rather than the SDK's dump.
    /// </summary>
    internal static Exception Translate(Exception exception, string? profile)
        => exception is DynamoDbSignInException || !IsSignInFailure(exception)
            ? exception
            : new DynamoDbSignInException(profile, IsProfileMissing(exception), exception);

    // An access-key id has a fixed shape (four letters naming the principal type, then 16 more); a secret or
    // token is only recognisable by what precedes it, so those are redacted by label.
    private static readonly Regex AccessKeyId = new(
        @"\b(?:AKIA|ASIA|AIDA|AROA|AGPA|ANPA|ANVA|APKA)[A-Z0-9]{12,}\b", RegexOptions.CultureInvariant);

    private static readonly Regex LabelledSecret = new(
        @"(?<key>aws[_ ]?secret[_ ]?access[_ ]?key|secret[_ ]?access[_ ]?key|secret[_ ]?key|session[_ ]?token|security[_ ]?token|x-amz-security-token|access[_ ]?token|bearer)\s*[=:]\s*(?<value>[^\s,;""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Redact anything in <paramref name="message"/> that looks like AWS key material, then the
    /// connection-string passwords <see cref="SafeErrorText"/> already knows.</summary>
    internal static string Scrub(string message)
    {
        var text = AccessKeyId.Replace(message, "***");
        text = LabelledSecret.Replace(text, m => $"{m.Groups["key"].Value}=***");
        return SafeErrorText.Redact(text);
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            if (!seen.Add(e))
            {
                continue;
            }
            yield return e;
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.Reverse())
                {
                    pending.Push(inner);
                }
            }
            else if (e.InnerException is { } next)
            {
                pending.Push(next);
            }
        }
    }
}

/// <summary>
/// The SDK could not sign this connection in. The message is the finished sentence
/// (<see cref="DynamoDbErrors.SignInSentence"/>) and the SDK's exception is kept as the inner one for the
/// classifier — never formatted into the text.
/// </summary>
internal sealed class DynamoDbSignInException : Exception
{
    public string? Profile { get; }

    /// <summary>True when the profile does not exist, as opposed to existing and failing.</summary>
    public bool ProfileMissing { get; }

    public DynamoDbSignInException(string? profile, bool profileMissing, Exception? inner = null)
        : base(DynamoDbErrors.SignInSentence(profile, profileMissing), inner)
    {
        Profile = profile;
        ProfileMissing = profileMissing;
    }
}

/// <summary>
/// The connection's own settings cannot be honoured (an <c>http://</c> endpoint that is not loopback, a
/// malformed endpoint, no region). The message is the whole sentence; nothing here is an AWS fault, so it
/// classifies <see cref="DbErrorKind.Unknown"/> and is reported as-is.
/// </summary>
internal sealed class DynamoDbConfigurationException : Exception
{
    public DynamoDbConfigurationException(string message) : base(message)
    {
    }
}
