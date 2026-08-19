namespace Bearing.Core.Workspace;

/// <summary>
/// Stores connection passwords outside any shareable file. Keyed by connection id so the key
/// travels with the project (in project.json) while the secret never does.
/// </summary>
public interface ISecretStore
{
    /// <summary>Persist a password. Throws <see cref="SecretStorageRefusedException"/> when
    /// <see cref="CanStore"/> is false — a store that cannot keep a secret safely refuses loudly rather
    /// than writing it somewhere weaker.</summary>
    Task SetPasswordAsync(Guid connectionId, string password, CancellationToken ct);

    Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken ct);
    Task DeleteAsync(Guid connectionId, CancellationToken ct);

    /// <summary>True for a real OS keychain; false when none could be reached, in which case nothing is
    /// stored at all (surface this to the user).</summary>
    bool IsSecure { get; }

    /// <summary>
    /// Whether <see cref="SetPasswordAsync"/> will actually store anything. False when no OS credential
    /// store could be reached: there is no second-choice place to put a password — writing one outside the
    /// keychain is plaintext with extra steps — so connections must use a credential kind that keeps the
    /// secret in memory (prompt) instead.
    /// </summary>
    bool CanStore { get; }

    /// <summary>
    /// Why this store can't keep a secret, in the words of whatever refused (redacted), or null when it can.
    /// Carried to the UI so a warning can say what actually happened instead of asserting a cause it never
    /// checked: a locked collection, a helper that isn't installed and a platform with no store at all read
    /// identically as "no keyring" and want completely different advice.
    /// </summary>
    string? UnavailableReason => null;
}

/// <summary>
/// Thrown when a password was offered to a store that <see cref="ISecretStore.CanStore"/> says can't hold
/// it. Callers treat this as "the connection saved, the password didn't" — it is a policy outcome, not a
/// failure to be logged as an error. There is no override: with no keychain the password is prompted for
/// and kept in memory for the session.
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

    /// <summary>
    /// Delete a project's directory and everything under it — scripts, scratch and session state included.
    /// Irreversible: the caller confirms first. Implementations must refuse a directory that isn't
    /// recognisably a project, so a wrong path can never take an unrelated folder with it.
    /// </summary>
    Task DeleteAsync(string directory, CancellationToken ct);
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

    /// <summary>Drop an entry from the list — used to prune projects that no longer exist on disk, so a
    /// deleted folder stops being offered after the first time it's noticed.</summary>
    Task RemoveAsync(string directory, CancellationToken ct);
}
