using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bearing.Core.Data;

namespace Bearing.App.Connections;

/// <summary>
/// Copying connections to and from the clipboard as text (#56). Plain JSON rather than a private in-process
/// format on purpose: the useful version of "copy a connection" is pasting it into a chat message or a
/// ticket for a colleague, which an in-process format cannot do, and a colleague pasting it back has to get
/// the same connection out.
///
/// <para><b>No passwords, ever.</b> The clipboard is world-readable to every process on the machine and
/// survives in clipboard managers, which is the opposite of the posture in §1.1 — a password reaches the OS
/// keychain and nowhere else. The identity goes too: an <see cref="ConnectionInfo.Id"/> is the secret-store
/// lookup key, so pasting one back would point a second connection at the first one's password. Paste mints
/// a fresh Guid.</para>
///
/// <para>Pure and self-contained, so the round trip is testable without a clipboard, a window, or a live
/// server (§2.5, §4.3).</para>
/// </summary>
public static class ConnectionClipboard
{
    /// <summary>Marks the payload as ours, so <see cref="TryRead"/> can decline arbitrary JSON that happens
    /// to have the right field names rather than pasting a half-populated connection.</summary>
    public const string Kind = "bearing.connections";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>One connection's shareable shape. Deliberately not <see cref="ConnectionInfo"/>: that record
    /// carries the id, and a payload that can't express one can't leak one.</summary>
    public sealed record Entry
    {
        public string Name { get; init; } = "";
        public string ProviderId { get; init; } = "postgres";
        public string Host { get; init; } = "localhost";
        public int Port { get; init; } = 5432;
        public string Database { get; init; } = "";
        public string User { get; init; } = "";
        public string? Folder { get; init; }
        public string? Environment { get; init; }
        public string? EnvironmentColor { get; init; }
        public bool RequireWriteConfirmation { get; init; }
        public CredentialKind CredentialKind { get; init; } = CredentialKind.StoredPassword;
        public Dictionary<string, string> Options { get; init; } = new();
    }

    private sealed record Payload(string Kind, int Version, List<Entry> Connections);

    /// <summary>Render connections as clipboard text.</summary>
    public static string Write(IEnumerable<ConnectionInfo> connections)
        => JsonSerializer.Serialize(
            new Payload(Kind, 1, connections.Select(ToEntry).ToList()), Options);

    /// <summary>
    /// Parse clipboard text back into connections, each with a <b>fresh</b> id and no password. Returns
    /// false for anything that isn't one of our payloads — malformed JSON, another app's JSON, or a plain
    /// string — so a paste gesture over unrelated clipboard content does nothing rather than something
    /// surprising.
    /// </summary>
    public static bool TryRead(string? text, out IReadOnlyList<ConnectionInfo> connections)
    {
        connections = Array.Empty<ConnectionInfo>();
        if (string.IsNullOrWhiteSpace(text)) return false;

        Payload? payload;
        try { payload = JsonSerializer.Deserialize<Payload>(text, Options); }
        catch (JsonException) { return false; }

        if (payload is null
            || !string.Equals(payload.Kind, Kind, StringComparison.Ordinal)
            || payload.Connections is not { Count: > 0 })
            return false;

        connections = payload.Connections.Select(FromEntry).ToList();
        return true;
    }

    private static Entry ToEntry(ConnectionInfo c) => new()
    {
        Name = c.Name,
        ProviderId = c.ProviderId,
        Host = c.Host,
        Port = c.Port,
        Database = c.Database,
        User = c.User,
        Folder = c.Folder,
        Environment = c.Environment,
        EnvironmentColor = c.EnvironmentColor,
        RequireWriteConfirmation = c.RequireWriteConfirmation,
        CredentialKind = c.CredentialKind,
        Options = c.Options.ToDictionary(kv => kv.Key, kv => kv.Value),
    };

    private static ConnectionInfo FromEntry(Entry e) => new()
    {
        Id = Guid.NewGuid(),   // never the source's — that id is its secret-store key
        Name = string.IsNullOrWhiteSpace(e.Name) ? "Connection" : e.Name,
        ProviderId = string.IsNullOrWhiteSpace(e.ProviderId) ? "postgres" : e.ProviderId,
        Host = e.Host,
        Port = e.Port,
        Database = e.Database,
        User = e.User,
        Folder = FolderPath.Normalize(e.Folder),
        Environment = e.Environment,
        EnvironmentColor = e.EnvironmentColor,
        RequireWriteConfirmation = e.RequireWriteConfirmation,
        CredentialKind = e.CredentialKind,
        Options = e.Options,
    };
}
