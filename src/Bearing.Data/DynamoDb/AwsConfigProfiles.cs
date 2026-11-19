using System.Text;

namespace Bearing.Data.DynamoDb;

/// <summary>A profile named in the AWS shared config file, and the region it sets, if any. Nothing else from the
/// file is kept (§1.9c).</summary>
public sealed record AwsConfigProfile(string Name, string? Region);

/// <summary>
/// The profile names (and their regions) in the AWS shared <b>config</b> file, for the connection editor's
/// AWS-profile suggestions.
/// <para>
/// <b>The config file only, never the credentials file</b> (§1.1 / §1.9c). <see cref="ConfigPath"/> is
/// <c>AWS_CONFIG_FILE</c> or <c>~/.aws/config</c> and nothing else; the SDK's profile-listing APIs
/// (<c>CredentialProfileStoreChain</c>, <c>SharedCredentialsFile</c>) are not used because they also read the
/// credentials file and would put access keys into Bearing's memory. A profile that exists only in the
/// credentials file is therefore not listed; the box still takes it typed.
/// </para>
/// <para>
/// Parsed here, minimally: <c>[default]</c> and <c>[profile NAME]</c> headers, and within them only the
/// <c>region</c> key. Every other section and key is skipped without its value being kept. A missing,
/// unreadable or oversized file is an empty list — the editor then shows a plain text box and says nothing,
/// since a missing config is the normal state for someone signing in another way. The file's text is never
/// logged.
/// </para>
/// </summary>
public static class AwsConfigProfiles
{
    /// <summary>A config file larger than this is not read at all. Real ones are a few kilobytes.</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>The environment variable the AWS SDK and CLI read for the config file's location.</summary>
    public const string ConfigFileVariable = "AWS_CONFIG_FILE";

    private static readonly char[] Blanks = [' ', '\t'];

    /// <summary>
    /// Where the config file is: <c>AWS_CONFIG_FILE</c> when set, else <c>.aws/config</c> under
    /// <paramref name="home"/>. Null when neither is known. Never the credentials file — this method does not
    /// read <c>AWS_SHARED_CREDENTIALS_FILE</c>, and it is the only place a path is built.
    /// </summary>
    public static string? ConfigPath(Func<string, string?> environment, string? home)
    {
        if (environment(ConfigFileVariable) is { } set && !string.IsNullOrWhiteSpace(set))
            return set.Trim();
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".aws", "config");
    }

    /// <summary>The profiles in this user's config file — the editor's source, through
    /// <see cref="DynamoDbProvider"/>.</summary>
    public static IReadOnlyList<AwsConfigProfile> ReadDefault()
        => Read(ConfigPath(Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    /// <summary>The profiles in the file at <paramref name="path"/>, or an empty list when it is missing,
    /// unreadable or larger than <see cref="MaxBytes"/>.</summary>
    public static IReadOnlyList<AwsConfigProfile> Read(string? path)
    {
        if (string.IsNullOrEmpty(path)) return [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > MaxBytes) return [];
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or ArgumentException or NotSupportedException)
        {
            // Missing (FileNotFound / DirectoryNotFound are IOExceptions), locked, denied, or a malformed
            // AWS_CONFIG_FILE value: there is nothing to suggest, and the box still takes a typed profile.
            return [];
        }
    }

    /// <summary>
    /// The profiles named in config-file <paramref name="text"/>, in file order, each with its <c>region</c>
    /// when the section sets one. A profile named twice is listed once, the later region winning (as the file's
    /// readers merge it). Comment lines (<c>#</c>, <c>;</c>), blank lines, other sections and malformed lines
    /// are skipped. An indented line under a key with no value (a nested block such as <c>s3 =</c>) is not read
    /// as a top-level <c>region</c>.
    /// </summary>
    public static IReadOnlyList<AwsConfigProfile> Parse(string text)
    {
        var order = new List<string>();
        var regions = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? current = null;
        var nested = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or ';') continue;

            var indented = char.IsWhiteSpace(line[0]);
            if (indented && nested) continue;
            nested = false;

            if (trimmed[0] == '[')
            {
                current = ProfileName(trimmed);
                if (current is not null && !regions.ContainsKey(current))
                {
                    order.Add(current);
                    regions[current] = null;
                }
                continue;
            }

            if (current is null) continue;
            var equals = trimmed.IndexOf('=');
            if (equals <= 0) continue;

            var key = trimmed[..equals].Trim();
            var value = trimmed[(equals + 1)..].Trim();
            if (value.Length == 0)
            {
                nested = true;
                continue;
            }
            // Only the region is read; every other key's value is dropped right here (§1.9c).
            if (!string.Equals(key, "region", StringComparison.OrdinalIgnoreCase)) continue;
            // A region code has no whitespace, so anything after it is an inline comment.
            var end = value.IndexOfAny(Blanks);
            regions[current] = end < 0 ? value : value[..end];
        }

        return order.Select(name => new AwsConfigProfile(name, regions[name])).ToList();
    }

    /// <summary>The profile a section header names, or null for any other section (<c>sso-session</c>,
    /// <c>services</c>, …) and for a malformed header.</summary>
    private static string? ProfileName(string header)
    {
        var close = header.IndexOf(']');
        if (close < 0) return null;
        var inner = header[1..close].Trim();
        if (inner == "default") return "default";

        const string prefix = "profile";
        if (!inner.StartsWith(prefix, StringComparison.Ordinal) || inner.Length == prefix.Length
            || !char.IsWhiteSpace(inner[prefix.Length]))
            return null;
        var name = inner[prefix.Length..].Trim();
        return name.Length == 0 ? null : name;
    }
}
