using Squirrel.Core.Data;

namespace Squirrel.Core.Workspace;

/// <summary>
/// The shareable, committed part of a project (project.json). Connections carry NO passwords —
/// those live in the OS secret store keyed by <see cref="ConnectionInfo.Id"/>.
/// </summary>
public sealed record ProjectManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string Name { get; init; } = "Untitled";
    public List<ConnectionInfo> Connections { get; init; } = new();
}

/// <summary>An opened project: its directory plus the loaded manifest.</summary>
public sealed class Project
{
    public required string Directory { get; init; }
    public required ProjectManifest Manifest { get; set; }

    /// <summary>Shared, committable SQL scripts live here.</summary>
    public string ScriptsDirectory => Path.Combine(Directory, "scripts");
}

/// <summary>One open editor tab in the per-user session (never shared).</summary>
public sealed record OpenEditor
{
    /// <summary>Path to a saved script, relative to the project directory (null for scratch buffers).</summary>
    public string? ScriptPath { get; init; }

    /// <summary>Inlined text for an unsaved scratch buffer (null when backed by a file).</summary>
    public string? ScratchText { get; init; }

    /// <summary>Display name for a scratch buffer ("Scratch N" or a user rename); null for saved scripts.</summary>
    public string? ScratchName { get; init; }

    public int CaretOffset { get; init; }
    public Guid? ConnectionId { get; init; }
}

/// <summary>Per-user, gitignored session state ("resume where I left off").</summary>
public sealed record SessionState
{
    public Guid? ActiveConnectionId { get; init; }
    public List<OpenEditor> OpenEditors { get; init; } = new();
    public int SelectedEditorIndex { get; init; }
    public string? LastOpenedUtc { get; init; }

    /// <summary>Whether the connections/scripts side pane is expanded.</summary>
    public bool SidePaneOpen { get; init; } = true;

    /// <summary>Persisted width of the side pane, in pixels.</summary>
    public double SidePaneWidth { get; init; } = 260;
}
