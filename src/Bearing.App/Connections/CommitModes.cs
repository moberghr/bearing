using System;
using System.Collections.Generic;
using Bearing.Core.Data;

namespace Bearing.App.Connections;

/// <summary>
/// Commit mode flipped for this session only, per connection (#131). The saved
/// <see cref="ConnectionInfo.ManualCommit"/> is the default; an entry here overrides it until the app closes
/// or the connection is saved.
/// <para>
/// <b>Why an override rather than writing the field.</b> <c>project.json</c> is shared, and turning manual
/// commit on for one dangerous <c>UPDATE</c> is not a statement about how everyone else should work — the
/// same reason §1.4 keeps a security setting out of the options bag rather than the other way round. What
/// the dialog says stays what the project says.
/// </para>
/// <para>
/// <b>The cost, and what pays it.</b> Two sources of truth can disagree, so exactly one thing resolves them:
/// <c>WorkspaceContext.EffectiveConnection</c> applies the override the same way it applies the tab's active
/// database, and every call site downstream reads <c>ManualCommit</c> off that record without knowing an
/// override exists. Nothing else may ask this class directly except the UI that draws the mode.
/// </para>
/// <para>
/// <b>Saving the connection clears the override</b> (<see cref="Clear"/>), because the dialog is the
/// authoritative statement of what this connection is. Without that rule, unticking the box would appear to
/// do nothing.
/// </para>
/// <para>
/// <b>Everything but <see cref="IsManualCommit"/> takes an id, not a record</b>, and that is a guard rather
/// than a convenience: the records most of the app holds are <i>effective</i> ones, which already have the
/// override applied — so comparing an override against one would compare it with itself and report that
/// nothing was overridden. Resolving the saved record here is the only way to be sure which one is being
/// asked about, and it is why the first version of this reported that nothing was ever overridden.
/// </para>
/// </summary>
public sealed class CommitModes
{
    private readonly Dictionary<Guid, bool> _overrides = new();
    private readonly Func<Guid, ConnectionInfo?> _saved;

    /// <param name="saved">Resolves a connection id to the record <b>as saved</b> — never an effective one.</param>
    public CommitModes(Func<Guid, ConnectionInfo?> saved) => _saved = saved;

    /// <summary>Raised when an override is set or cleared — the toolbar pill reads from it.</summary>
    public event Action? Changed;

    /// <summary>
    /// Whether this connection is in manual-commit mode right now: the override if there is one, else what
    /// was saved. Takes the record because its caller —
    /// <c>WorkspaceContext.EffectiveConnection</c> — is holding the saved one already, and is the only
    /// caller there should be.
    /// </summary>
    public bool IsManualCommit(ConnectionInfo info)
        => _overrides.TryGetValue(info.Id, out var overridden) ? overridden : CommitPolicy.IsManualCommit(info);

    /// <summary>Whether the mode in force differs from the one saved on the connection — what the pill's
    /// tooltip says out loud, so a mode nobody can find in the dialog is never a mystery.</summary>
    public bool IsOverridden(Guid connectionId)
        => _overrides.TryGetValue(connectionId, out var overridden)
           && _saved(connectionId) is { } info
           && overridden != CommitPolicy.IsManualCommit(info);

    /// <summary>Put this connection in <paramref name="manualCommit"/> for the rest of the session. Setting
    /// it back to what was saved removes the entry rather than keeping a redundant one, so
    /// <see cref="IsOverridden"/> stops being true the moment the two agree again.</summary>
    public void Set(Guid connectionId, bool manualCommit)
    {
        if (_saved(connectionId) is { } info && manualCommit == CommitPolicy.IsManualCommit(info))
            _overrides.Remove(connectionId);
        else _overrides[connectionId] = manualCommit;
        Changed?.Invoke();
    }

    /// <summary>Drop any override for this connection — what saving it does.</summary>
    public void Clear(Guid connectionId)
    {
        if (_overrides.Remove(connectionId)) Changed?.Invoke();
    }
}
