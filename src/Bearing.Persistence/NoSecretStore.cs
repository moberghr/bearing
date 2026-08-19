using Bearing.Core.Workspace;

namespace Bearing.Persistence;

/// <summary>
/// The store used when this machine has no working OS credential store: it holds nothing.
/// <para>
/// There is no local file fallback and no opt-in to one. Anything Bearing could write outside the OS
/// keychain would be plaintext with extra steps, so a password offered here is refused
/// (<see cref="SecretStorageRefusedException"/>) and the connection is expected to prompt and keep the
/// secret in memory for the session. Reads return null and deletes are a no-op — there is nowhere for a
/// secret to be.
/// </para>
/// </summary>
public sealed class NoSecretStore : ISecretStore
{
    /// <param name="reason">What the platform store said when it was rejected (redacted), or null when this
    /// platform has none at all. Shown to the user, so the warning reports what happened rather than
    /// asserting a cause nobody checked.</param>
    public NoSecretStore(string? reason = null) => UnavailableReason = reason;

    public bool IsSecure => false;

    public bool CanStore => false;

    public string? UnavailableReason { get; }

    public Task SetPasswordAsync(Guid connectionId, string password, CancellationToken ct)
        => throw new SecretStorageRefusedException(
            "No system keyring is available, so this password was not saved. Bearing will ask for it when "
            + "you connect and keep it in memory for this session — use the \"Prompt each time\" credential "
            + "kind to make that explicit.");

    public Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken ct)
        => Task.FromResult<string?>(null);

    public Task DeleteAsync(Guid connectionId, CancellationToken ct) => Task.CompletedTask;
}
