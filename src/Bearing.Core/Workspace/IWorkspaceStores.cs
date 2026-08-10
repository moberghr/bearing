namespace Bearing.Core.Workspace;

/// <summary>
/// Stores connection passwords outside any shareable file. Keyed by connection id so the key
/// travels with the project (in project.json) while the secret never does.
/// </summary>
public interface ISecretStore
{
    /// <summary>Persist a password. Throws <see cref="SecretStorageRefusedException"/> when
    /// <see cref="CanStore"/> is false — a store that cannot keep a secret safely refuses loudly rather
    /// than writing it somewhere the user didn't agree to.</summary>
    Task SetPasswordAsync(Guid connectionId, string password, CancellationToken ct);

    Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken ct);
    Task DeleteAsync(Guid connectionId, CancellationToken ct);

    /// <summary>True for a real OS keychain; false for a local fallback (surface this to the user).</summary>
    bool IsSecure { get; }

    /// <summary>
    /// Whether <see cref="SetPasswordAsync"/> will actually store anything. False when the only place left
    /// to put a password is plaintext-equivalent on disk and the user hasn't opted into that, in which case
    /// connections must use a credential kind that keeps the secret in memory (prompt) instead. Reading and
    /// deleting stay available either way, so secrets stored before the opt-in was withdrawn still resolve
    /// and can still be cleared.
    /// </summary>
    bool CanStore { get; }
}

/// <summary>
/// Thrown when a password was offered to a store that <see cref="ISecretStore.CanStore"/> says can't hold
/// it. Callers treat this as "the connection saved, the password didn't" — it is a policy outcome, not a
/// failure to be logged as an error.
/// </summary>
public sealed class SecretStorageRefusedException : Exception
{
    public SecretStorageRefusedException(string message) : base(message) { }
}

public interface IProjectStore
{
    Task<Project> CreateAsync(string directory, string name, CancellationToken ct);
    Task<Project> OpenAsync(string directory, CancellationToken ct);
    Task SaveAsync(Project project, CancellationToken ct);
}

public interface ISessionStore
{
    Task<SessionState?> LoadAsync(string projectDirectory, CancellationToken ct);
    Task SaveAsync(string projectDirectory, SessionState state, CancellationToken ct);

    /// <summary>Synchronous save for shutdown paths, where blocking on async would deadlock the UI thread.</summary>
    void Save(string projectDirectory, SessionState state);
}

/// <summary>
/// Reads and writes the app-global preferences file. Best-effort in both directions: a missing or
/// malformed file loads as defaults, so a hand-edit can never stop the app from starting.
/// </summary>
public interface IAppSettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);

    /// <summary>Where the file lives — shown in the settings window, since a few options are still
    /// file-edit only.</summary>
    string Location { get; }
}

public interface IRecentProjects
{
    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct);
    Task AddAsync(string directory, CancellationToken ct);
}
