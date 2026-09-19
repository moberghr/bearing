using System.Collections.Generic;
using Bearing.Core.Data;

namespace Bearing.App.Connections;

/// <summary>One entry of the connection editor's Credential dropdown.</summary>
/// <param name="Kind">The value saved on the connection.</param>
/// <param name="Label">What the dropdown shows.</param>
public sealed record CredentialKindOption(CredentialKind Kind, string Label);

/// <summary>
/// Which credential kinds an engine can actually offer, and what each is called. Pure, so the list the
/// dropdown shows is testable without a window (§4.3) and the dialog never has to know which engines have
/// integrated authentication.
/// <para>
/// The list is not fixed because <see cref="CredentialKind"/> is not uniformly supported:
/// <see cref="CredentialKind.Integrated"/> is the OS identity, which only an engine whose driver can
/// negotiate it (<see cref="IDbProvider.SupportsIntegratedAuth"/>) can use. Offering it on Postgres would
/// be a setting that silently fails to connect. <see cref="CredentialKind.EntraToken"/> is gated the same
/// way, on <see cref="IDbProvider.SupportsEntraToken"/>: the token needs a driver that will take it, and
/// SqlClient wants it on <c>SqlConnection.AccessToken</c> rather than as a password.
/// </para>
/// <para>
/// Every kind except <see cref="CredentialKind.StoredPassword"/> keeps nothing on disk, which is why the
/// dialog's password box and its no-keychain warning are both driven by "is this the stored-password kind"
/// rather than by a per-kind list: an <see cref="CredentialKind.Integrated"/> connection resolves no
/// secret at all, so warning it about an unreachable keychain would be warning about something it never
/// touches (§1.1).
/// </para>
/// </summary>
public static class CredentialKindOptions
{
    /// <summary>The dropdown's entries for <paramref name="provider"/>, in display order. The first is the
    /// default for a new connection when a keychain is reachable.</summary>
    public static IReadOnlyList<CredentialKindOption> For(IDbProvider provider)
    {
        var options = new List<CredentialKindOption>(4)
        {
            new(CredentialKind.StoredPassword, "Stored password"),
            new(CredentialKind.Prompt, "Prompt each time (not saved)"),
        };
        if (provider.SupportsEntraToken)
            options.Add(new(CredentialKind.EntraToken, "Microsoft Entra token (az login)"));
        if (provider.SupportsIntegratedAuth)
            options.Add(new(CredentialKind.Integrated, "Windows / integrated authentication"));
        return options;
    }

    /// <summary>Whether a kind stores a password — the one kind with a password box and, when no keychain
    /// is reachable, the one kind the dialog warns about.</summary>
    public static bool KeepsAStoredPassword(CredentialKind kind) => kind == CredentialKind.StoredPassword;
}
