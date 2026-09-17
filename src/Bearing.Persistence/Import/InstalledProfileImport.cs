using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Core.Workspace;

namespace Bearing.Persistence.Import;

/// <summary>One profile's connections, and where they came from.</summary>
/// <param name="Profile">The app directory the connections were read out of, for the dialog to name.</param>
/// <param name="Connections">Every connection found, deduplicated by id.</param>
/// <param name="Folders">The folders those connections are filed in.</param>
/// <param name="Projects">How many project files were read, so "nothing found" can say what it looked at.</param>
public sealed record ProfileConnections(
    string Profile,
    IReadOnlyList<ConnectionInfo> Connections,
    IReadOnlyList<string> Folders,
    int Projects)
{
    public static ProfileConnections None(string profile) => new(profile, [], [], 0);
}

/// <summary>How many saved passwords came across with an import, and what stopped the rest.</summary>
/// <param name="Copied">Connections whose password was read from the source profile and saved here.</param>
/// <param name="WithoutPassword">Connections the source profile had no saved password for — a normal
/// shape (a prompt-every-time connection), reported separately so it is not read as a failure.</param>
/// <param name="Replaced">How many of <paramref name="Copied"/> wrote over a <em>different</em> password
/// this profile already held. Counted because the old value is not recoverable: a password rotated here and
/// not in the installed app is silently replaced by the stale one, and the connection then fails to
/// authenticate with nothing on screen to explain it.</param>
/// <param name="Failed">How many copies failed. A count as well as a reason, because "some could not be
/// copied" over twelve connections is a different fact from one.</param>
/// <param name="Refused">The first redacted reason a copy failed, or null when none did.</param>
public sealed record SecretCarry(int Copied, int WithoutPassword, int Replaced, int Failed, string? Refused);

/// <summary>
/// Reads the connections belonging to <em>another</em> Bearing profile — in practice the installed app's,
/// from a build running out of source.
/// <para>
/// <c>BEARING_PROFILE</c> gives a dev build a fully separate config, data and secret namespace, which is what
/// stops running from source touching the real projects and query log. The cost is that a dev build starts
/// with no connections at all, so seeing what the app will actually look like means typing them in again.
/// This copies them across.
/// </para>
/// <para>
/// <b>Read-only, and it stays that way.</b> Nothing here creates or writes a path under the source profile:
/// the whole point of the isolation is that a build from source cannot disturb the installed one, and a
/// convenience that breached it would be worse than the typing it saves.
/// </para>
/// <para>
/// <b>Saved passwords travel with the connections</b> (approved 2026-09-12). They live in the OS keychain
/// under a key whose first segment is the profile — <c>bearing:connection:&lt;guid&gt;</c> against
/// <c>dev:connection:&lt;guid&gt;</c> — so a copied connection used to find nothing and prompt. It is the same
/// user, the same machine and the same credential store the user can already read in the OS credential UI,
/// and the import preserves <c>ConnectionInfo.Id</c>, so the copy is a read under one key and a write under
/// another (<see cref="CopyPasswordsAsync"/>).
/// </para>
/// <para>
/// <b>What that costs, stated once so it is not rediscovered:</b> the isolation used to mean a build from
/// source could not reach a production server without a human typing the password, and it no longer does —
/// the copy persists in the credential store and every later dev build reads it. That is the trade that was
/// approved, not an oversight. Nothing else about §1.1 moves: no password is written outside the secret
/// store, none is logged, and a store that refuses the write is reported rather than worked around.
/// </para>
/// </summary>
public static class InstalledProfileImport
{
    /// <summary>The profile a normal installation uses.</summary>
    public const string InstalledProfile = "bearing";

    /// <summary>
    /// Whether this process is running under a profile of its own, and so has an installed profile to import
    /// from. False in a real installation, which is what keeps the command out of one.
    /// </summary>
    public static bool IsSeparateProfile
        => !string.Equals(BearingPaths.AppDirName, InstalledProfile, StringComparison.Ordinal);

    /// <summary>
    /// Every connection the installed profile knows about: its default project, plus each project in its
    /// recent list. Deduplicated by id, so a connection reachable through two projects is offered once.
    /// </summary>
    public static async Task<ProfileConnections> ReadAsync(
        string profile = InstalledProfile, CancellationToken ct = default)
    {
        var (configDir, dataDir) = BearingPaths.RootsFor(profile);
        var store = new JsonProjectStore();

        var directories = new List<string> { Path.Combine(dataDir, "projects", "default") };
        directories.AddRange(await RecentAsync(configDir, ct).ConfigureAwait(false));

        var byId = new Dictionary<Guid, ConnectionInfo>();
        var folders = new List<string>();
        var read = 0;

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(directory, "project.json"))) continue;

            Project project;
            // Best-effort per project: one unreadable or half-written manifest must not cost the others
            // (§5.2). Nothing is being changed, so there is nothing to leave inconsistent.
            try { project = await store.OpenAsync(directory, ct).ConfigureAwait(false); }
            catch (Exception) { continue; }

            read++;
            foreach (var connection in project.Manifest.Connections) byId.TryAdd(connection.Id, connection);
            folders.AddRange(project.Manifest.ConnectionFolders);
        }

        return new ProfileConnections(
            profile,
            byId.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            read);
    }

    /// <summary>
    /// Copy each connection's saved password out of <paramref name="profile"/>'s credential store and into
    /// this profile's. <paramref name="connections"/> pairs the id the password is stored under with the id
    /// it landed on here — the two differ when the import updated a connection already present, which keeps
    /// its own id (and so its own key).
    /// <para>
    /// Value tuples rather than a record so the caller's own import type does not have to reach down a layer
    /// (§2.2). Best-effort per connection: one unreadable secret must not cost the rest, exactly as one
    /// unreadable manifest does not in <see cref="ReadAsync"/>.
    /// </para>
    /// </summary>
    /// <param name="source">The store to read from. Defaults to this platform's, keyed to
    /// <paramref name="profile"/>; a test supplies its own, since the real one can only be exercised on the
    /// machine that has it (§4.2).</param>
    public static async Task<SecretCarry> CopyPasswordsAsync(
        IReadOnlyList<(Guid Source, Guid Local)> connections,
        ISecretStore target,
        string profile = InstalledProfile,
        ISecretStore? source = null,
        CancellationToken ct = default)
    {
        if (connections.Count == 0) return new SecretCarry(0, 0, 0, 0, null);

        // Unprobed on purpose: probing writes a throwaway secret, and this profile is one we only read
        // (see SecretStoreFactory.UnprobedStoreFor).
        source ??= SecretStoreFactory.UnprobedStoreFor(profile);
        if (source is null)
            return new SecretCarry(0, 0, 0, 0, "this platform has no credential store to copy them from.");

        // Asked before the loop, not per connection: a store that keeps nothing (NoSecretStore, §1.1) throws
        // on every write, and reporting that once is the honest answer rather than n identical failures.
        if (!target.CanStore)
            return new SecretCarry(0, 0, 0, 0, "this session has no keychain to save them in.");

        int copied = 0, missing = 0, replaced = 0, failed = 0;
        string? refused = null;

        foreach (var (sourceId, localId) in connections)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var password = await source.GetPasswordAsync(sourceId, ct).ConfigureAwait(false);
                // Not every connection has one saved — a prompt-every-time connection is a normal shape, and
                // it is not a failure to report.
                if (string.IsNullOrEmpty(password)) { missing++; continue; }

                // Read before writing so an overwrite can be reported. Re-importing is the normal case, and
                // the value about to be lost is one only this profile has.
                string? held = null;
                try { held = await target.GetPasswordAsync(localId, ct).ConfigureAwait(false); }
                catch (Exception) { /* unknown, not "none": simply don't claim a replacement happened */ }

                await target.SetPasswordAsync(localId, password, ct).ConfigureAwait(false);
                copied++;
                if (!string.IsNullOrEmpty(held) && !string.Equals(held, password, StringComparison.Ordinal))
                    replaced++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                // Redacted (§1.1): this string reaches the status bar, and the exception came from a call
                // whose argument was a password.
                refused ??= Bearing.Core.Data.SafeErrorText.Of(ex);
            }
        }

        return new SecretCarry(copied, missing, replaced, failed, refused);
    }

    private static async Task<IReadOnlyList<string>> RecentAsync(string configDir, CancellationToken ct)
    {
        try { return await new FileRecentProjects(Path.Combine(configDir, "recent.json")).ListAsync(ct).ConfigureAwait(false); }
        catch (Exception) { return []; }
    }
}
