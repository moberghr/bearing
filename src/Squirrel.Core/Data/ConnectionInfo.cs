namespace Squirrel.Core.Data;

/// <summary>
/// Non-secret connection settings, as persisted in a project file. The password is NEVER here —
/// it is fetched from <c>ISecretStore</c> keyed by <see cref="Id"/>.
/// </summary>
public sealed record ConnectionInfo
{
    /// <summary>Stable identity; also the secret-store lookup key. Travels with the project.</summary>
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Provider id, e.g. "postgres".</summary>
    public required string ProviderId { get; init; }

    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5432;
    public string Database { get; init; } = "";
    public string User { get; init; } = "";

    /// <summary>Free-form environment label (e.g. "local", "staging", "production"); null = untagged.</summary>
    public string? Environment { get; init; }

    /// <summary>Hex color for the environment badge (e.g. "#E53935"); null = neutral.</summary>
    public string? EnvironmentColor { get; init; }

    /// <summary>Provider-specific extra options (e.g. sslmode, search_path).</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>Describes one field in a provider's connection dialog (drives the UI generically).</summary>
public sealed record ConnectionField(
    string Key,
    string Label,
    ConnectionFieldKind Kind,
    bool Required,
    string? Default = null);

public enum ConnectionFieldKind
{
    Text,
    Number,
    Password,
    Boolean,
    Choice,
}
