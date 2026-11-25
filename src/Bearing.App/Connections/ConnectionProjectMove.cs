using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Services;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Sessions;

namespace Bearing.App.Connections;

/// <summary>
/// "Move to project…" on a server row: take a connection out of the project on screen and put it in another
/// one on this machine, saved password and SSH secret included.
///
/// <para><b>The id is kept</b> whenever the target doesn't already use it. The id is the secret-store key and
/// the query log's connection key, so keeping it carries the password and SSH secret by construction (same
/// user, same keychain — nothing is read or written, nothing prompts) and the connection's history stays its
/// own. Only when the target already holds a connection with that id (a project.json copied by hand) is a
/// fresh id minted; then both secrets are read from the keychain under the old id and written under the new
/// one, the way Duplicate does, and the old id's entries are left alone because the target's connection
/// still uses them.</para>
///
/// <para><b>Removing it from the source is not a delete.</b> A delete removes the secrets; a move must not,
/// since the moved connection is still keyed by them. It is a delete under §1.10's guard table otherwise:
/// the source's tabs on it lose their connection (they belong to the source project, whose connection list
/// no longer has it), any transaction held on one is rolled back and reported, and its sessions are evicted.
/// A query still running on it refuses the move.</para>
///
/// <para><see cref="ConnectionInfo.ExternalAccess"/> is <b>kept</b>: same user, same machine — the person
/// moving it already made that decision, and silently withdrawing it would break a script that relies on it.
/// (The export file drops it because it crosses to a machine where nobody made that decision.)</para>
///
/// <para>Ordered so a failure never loses the connection: the target is written first, and only once that
/// succeeded is anything removed from the source. Every failure is a status line (§5.2), never a throw.</para>
/// </summary>
public sealed class ConnectionProjectMove
{
    private readonly WorkspaceContext _ctx;

    public ConnectionProjectMove(WorkspaceContext ctx) => _ctx = ctx;

    /// <summary>How the connection will land in the target: its id (kept unless taken there) and a name that
    /// doesn't collide with one already there (the import/paste rule, <see cref="ConnectionNames"/>).</summary>
    public sealed record Plan(ConnectionInfo Landed, bool IdReminted, bool Renamed);

    /// <summary>Plan the landing. Pure.</summary>
    public static Plan PlanMove(ConnectionInfo source, ProjectManifest target)
    {
        var reminted = target.Connections.Any(c => c.Id == source.Id);
        var name = ConnectionNames.Unique(target.Connections.Select(c => c.Name), source.Name, alwaysSuffix: false);
        var landed = source with
        {
            Id = reminted ? Guid.NewGuid() : source.Id,
            Name = name,
            Folder = FolderPath.Normalize(source.Folder),
        };
        return new Plan(landed, reminted, !string.Equals(name, source.Name, StringComparison.Ordinal));
    }

    /// <summary>What a move did.</summary>
    /// <param name="DetachedTabs">Source tabs that pointed at the connection and now point at nothing — the
    /// caller re-renders their headers.</param>
    public sealed record Outcome(bool Moved, string Status, IReadOnlyList<EditorTabViewModel> DetachedTabs)
    {
        internal static Outcome Refused(string status) => new(false, status, []);
    }

    /// <summary>Move connection <paramref name="id"/> from the project on screen into the project at
    /// <paramref name="targetDirectory"/>.</summary>
    public async Task<Outcome> MoveAsync(Guid id, string targetDirectory)
    {
        if (_ctx.Project is not { } source) return Outcome.Refused("No project is open.");
        if (source.Manifest.Connections.FirstOrDefault(c => c.Id == id) is not { } connection)
            return Outcome.Refused("That connection is no longer in this project.");
        if (SamePath(source.Directory, targetDirectory))
            return Outcome.Refused($"'{connection.Name}' is already in this project.");

        var tabs = _ctx.Tabs.Where(t => t.ConnectionId == id).ToList();
        if (tabs.Any(t => t.IsRunning))
            return Outcome.Refused(
                $"A query is still running on '{connection.Name}' — cancel it or let it finish before moving it.");

        // An open project's manifest is the live one — writing a fresh copy from disk would be overwritten by
        // its next save. Only a project not open this session is read from disk.
        Project target;
        try
        {
            target = _ctx.Find(targetDirectory)?.Project
                     ?? await _ctx.ProjectStore.OpenAsync(targetDirectory, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return Outcome.Refused($"Could not open that project: {SafeErrorText.Of(ex)}");
        }

        var plan = PlanMove(connection, target.Manifest);
        var notes = new List<string>();
        if (plan.IdReminted) notes.Add(await CarrySecretsAsync(connection, plan.Landed.Id));
        if (plan.Renamed) notes.Add($"named '{plan.Landed.Name}' there, as '{connection.Name}' was taken");

        // 1. The target first. A failure here leaves the source exactly as it was.
        var addedFolder = plan.Landed.Folder is { } folder
                          && !target.Manifest.ConnectionFolders.Any(f =>
                              string.Equals(FolderPath.Normalize(f), folder, StringComparison.OrdinalIgnoreCase));
        target.Manifest.Connections.Add(plan.Landed);
        if (addedFolder) target.Manifest.ConnectionFolders.Add(plan.Landed.Folder!);
        try
        {
            await _ctx.ProjectStore.SaveAsync(target, CancellationToken.None);
        }
        catch (Exception ex)
        {
            target.Manifest.Connections.RemoveAll(c => ReferenceEquals(c, plan.Landed));
            if (addedFolder) target.Manifest.ConnectionFolders.Remove(plan.Landed.Folder!);
            if (plan.IdReminted) await ForgetCarriedAsync(plan.Landed.Id);
            return Outcome.Refused(
                $"Could not move '{connection.Name}': the project '{target.Manifest.Name}' could not be saved: "
                + $"{SafeErrorText.Of(ex)}. It is still here.");
        }

        // 2. Settle what the source held on it (§1.10: a deleted connection rolls back and reports).
        var rolled = await _ctx.Transactions.RollbackForTabsAsync(tabs, CancellationToken.None);
        if (rolled > 0)
            notes.Add(rolled == 1 ? "rolled back 1 uncommitted transaction" : $"rolled back {rolled} uncommitted transactions");
        await _ctx.Sessions.EvictConnectionAsync(id);
        _ctx.CommitModes.Clear(id);
        foreach (var tab in tabs) tab.ConnectionId = null;
        if (_ctx.DefaultConnectionId == id) _ctx.DefaultConnectionId = null;

        // 3. Out of the source — without touching the keychain: the moved connection is still keyed by it.
        source.Manifest.Connections.RemoveAll(c => c.Id == id);
        string status;
        try
        {
            await _ctx.ProjectStore.SaveAsync(source, CancellationToken.None);
            status = $"Moved '{connection.Name}' to '{target.Manifest.Name}'.";
        }
        catch (Exception ex)
        {
            status = $"Moved '{connection.Name}' to '{target.Manifest.Name}', but this project could not be saved "
                     + $"({SafeErrorText.Of(ex)}) — it may still list it after a restart.";
        }

        var said = notes.Where(n => n.Length > 0).ToList();
        if (said.Count > 0) status += " " + Capitalise(string.Join("; ", said)) + ".";
        return new Outcome(true, status, tabs);
    }

    /// <summary>Copy the password and SSH secret from <paramref name="source"/>'s id to <paramref name="newId"/>,
    /// the same reads and writes Duplicate does. Returns a clause for the status line ("" when all went
    /// through or there was nothing to carry).</summary>
    private async Task<string> CarrySecretsAsync(ConnectionInfo source, Guid newId)
    {
        const string why = "a connection with the same id was already there, so it has a new one";
        if (_ctx.Secrets is not { } store) return why;

        var problems = new List<string>();
        async Task CarryAsync(Guid from, Guid to, string what)
        {
            try
            {
                if (await store.GetPasswordAsync(from, CancellationToken.None) is { } secret)
                    await store.SetPasswordAsync(to, secret, CancellationToken.None);
            }
            catch (SecretStorageRefusedException)
            {
                problems.Add($"its {what} could not be saved (no keyring) and will be asked for");
            }
            catch (Exception ex)
            {
                problems.Add($"its {what} could not be copied ({SafeErrorText.Of(ex)}) and will be asked for");
            }
        }

        if (CredentialKindOptions.KeepsAStoredPassword(source.CredentialKind))
            await CarryAsync(source.Id, newId, "password");
        if (source.SshTunnel is not null && !SshTunnels.KeepsSecretInMemoryOnly(source))
            await CarryAsync(SshSecretId.For(source.Id), SshSecretId.For(newId), "SSH secret");

        return problems.Count == 0 ? why : why + "; " + string.Join("; ", problems);
    }

    /// <summary>Undo <see cref="CarrySecretsAsync"/> after the target could not be saved. Best-effort.</summary>
    private async Task ForgetCarriedAsync(Guid newId)
    {
        if (_ctx.Secrets is not { } store) return;
        try
        {
            await store.DeleteAsync(newId, CancellationToken.None);
            await store.DeleteAsync(SshSecretId.For(newId), CancellationToken.None);
        }
        catch { /* best-effort (§5.2): an orphaned entry under an id nothing uses is harmless */ }
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal); }
        catch { return string.Equals(a, b, StringComparison.Ordinal); }
    }
}
