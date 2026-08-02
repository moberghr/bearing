using System.Threading;
using System.Threading.Tasks;
using Squirrel.Core.Data;

namespace Squirrel.App.Connections;

/// <summary>Asks the user for a connection password at connect time (for
/// <see cref="CredentialKind.Prompt"/> connections). Implemented in the UI layer; returns null when the
/// user cancels the prompt.</summary>
public interface ICredentialPrompt
{
    Task<string?> RequestPasswordAsync(ConnectionInfo info, string? message, CancellationToken ct);
}
