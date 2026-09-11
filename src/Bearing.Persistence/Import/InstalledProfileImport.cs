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
/// <b>No secrets travel.</b> Passwords live in the OS keychain under a service name that carries the profile,
/// so a copied connection finds nothing there and prompts — which is correct, not a gap to paper over.
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

    private static async Task<IReadOnlyList<string>> RecentAsync(string configDir, CancellationToken ct)
    {
        try { return await new FileRecentProjects(Path.Combine(configDir, "recent.json")).ListAsync(ct).ConfigureAwait(false); }
        catch (Exception) { return []; }
    }
}
