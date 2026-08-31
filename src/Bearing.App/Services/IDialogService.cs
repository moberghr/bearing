using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Results;
using Bearing.App.Views;
using Bearing.Core.Data;

namespace Bearing.App.Services;

/// <summary>
/// View-layer dialog interactions, expressed so callers (view-models and code-behind) can request UI
/// without newing up dialog windows or storage pickers themselves. Implemented by <c>Views/DialogService</c>,
/// which owns dialog construction and resolves the active window. Centralising this is phase 5 of the MVVM
/// refactor (docs/mvvm-refactor-plan.md) — the code-behind stops knowing concrete dialog types.
/// Implementations with no window (headless/tests) proceed/return sensibly (see <see cref="ConfirmWriteAsync"/>).
/// </summary>
/// <summary>
/// What the user chose when asked about closing a tab that holds unsaved work.
/// <see cref="Cancel"/> is deliberately the zero value: a dialog dismissed via the title bar returns
/// <c>default</c>, and "don't close" is the only safe reading of a dismissal.
/// </summary>
public enum CloseChoice
{
    /// <summary>Don't close.</summary>
    Cancel = 0,

    /// <summary>Save first, then close. A scratch tab still needs a destination picked.</summary>
    Save,

    /// <summary>Close and throw the work away.</summary>
    Discard,
}

/// <summary>
/// What the user chose when asked about removing a project. <see cref="Cancel"/> is the zero value, so a
/// dialog dismissed any way at all does nothing — and unlike <see cref="CloseChoice"/>, a headless caller
/// gets this one too: nothing may delete a folder without someone saying so.
/// </summary>
public enum ProjectRemoval
{
    /// <summary>Leave the project alone.</summary>
    Cancel = 0,

    /// <summary>Forget it — drop the recent-list entry and leave the files where they are.</summary>
    FromList,

    /// <summary>Delete the project directory as well.</summary>
    FromDisk,
}

/// <summary>
/// Where a connection password would end up, as the connection editor needs to see it. Either a real OS
/// keychain (both flags true), or no keychain could be reached — in which case nothing is stored anywhere
/// and the connection must prompt for the password instead. <see cref="Reason"/> carries what the store
/// actually said, so the warning can explain itself rather than guess.
/// </summary>
public readonly record struct SecretStoragePosture(bool Secure, bool CanStore, string? Reason = null)
{
    /// <summary>The posture to assume when no store is attached yet (headless/tests): a real keychain, so
    /// nothing warns and nothing is blocked.</summary>
    public static SecretStoragePosture Keychain => new(Secure: true, CanStore: true);
}

public interface IDialogService
{
    /// <summary>Confirm a write — a risky batch on a guarded connection, or any inline-edit save — showing
    /// the statements it is about to run. True = proceed. Implementations with no window (headless/tests)
    /// proceed silently.</summary>
    Task<bool> ConfirmWriteAsync(WriteConfirmation request);

    /// <summary>Confirm throwing away queries that are still running, before an action that would cancel
    /// them. <paramref name="tabName"/> null asks about quitting (<paramref name="runningCount"/> tabs);
    /// non-null asks about closing that one tab. True = cancel the run(s) and proceed.
    /// Implementations with no window (headless/tests) proceed — a headless close still closes.</summary>
    Task<bool> ConfirmCancelRunningAsync(int runningCount, string? tabName = null);

    /// <summary>Ask whether to save before closing a tab that holds unsaved work.
    /// <paramref name="tabName"/> is the tab header, so the prompt names what is about to be lost.
    /// Implementations with no window (headless/tests) return <see cref="CloseChoice.Discard"/> —
    /// the pre-prompt behaviour, so a headless close still closes.</summary>
    Task<CloseChoice> ConfirmCloseTabAsync(string tabName);

    /// <summary>Open the add/edit connection dialog. Returns the dialog result (add/update or delete), or
    /// null if cancelled. <paramref name="test"/> backs the dialog's Test button; <paramref name="storage"/>
    /// decides which credential kind a new connection starts on and what the dialog warns about.</summary>
    Task<ConnectionDialogResult?> ShowConnectionDialogAsync(
        ConnectionInfo? existing,
        string? existingPassword,
        Func<ConnectionInfo, string?, CancellationToken, Task<bool>> test,
        SecretStoragePosture storage);

    /// <summary>Confirm deleting a script file. True = delete it. Implementations with no window
    /// (headless/tests) return false: as with <see cref="ConfirmRemoveProjectAsync"/>, nothing deletes a file
    /// without someone saying so.</summary>
    Task<bool> ConfirmDeleteScriptAsync(string fileName);

    /// <summary>Ask what to do with a project the user wants gone: forget it, or delete its folder too.
    /// <paramref name="directory"/> is shown, since that is what a delete would remove. Implementations with
    /// no window (headless/tests) return <see cref="ProjectRemoval.Cancel"/> — deliberately the opposite of
    /// <see cref="ConfirmCloseTabAsync"/>: a headless close still closes, but nothing headless deletes files.</summary>
    Task<ProjectRemoval> ConfirmRemoveProjectAsync(string name, string directory);

    /// <summary>Prompt for a single line of text (rename, new folder/script, project name). Null if cancelled.</summary>
    Task<string?> ShowTextPromptAsync(string prompt, string initial = "");

    /// <summary>Pick an existing folder (project open/new). Returns its local path, or null if cancelled.
    /// <paramref name="startDir"/> seeds the initial location — projects live next to each other, so the
    /// browser should open where they already are rather than at the picker's idea of home.</summary>
    Task<string?> PickFolderAsync(string title, string? startDir = null);

    /// <summary>Pick an existing .sql file to open. <paramref name="startDir"/> seeds the initial location.
    /// Returns the local path, or null if cancelled.</summary>
    Task<string?> PickOpenScriptAsync(string? startDir);

    /// <summary>Pick a destination for saving a .sql file. Returns the local path, or null if cancelled.</summary>
    Task<string?> PickSaveScriptAsync(string suggestedName, string? startDir);

    /// <summary>Pick a connections file to import from — DBeaver's data-sources.json (#72). Filtered to
    /// .json rather than to that exact name: the workspace path is user-configurable, and someone importing
    /// a copy a colleague sent will not have kept the filename.</summary>
    Task<string?> PickImportFileAsync(string? startDir);

    /// <summary>Pick a destination for an exported result set, filtered to <paramref name="format"/>'s file
    /// type. Returns the local path, or null if cancelled.</summary>
    Task<string?> PickExportFileAsync(string suggestedName, ExportFormat format);

    /// <summary>Show SQL in a read-only, monospace preview window (non-modal; selectable to copy).</summary>
    void ShowSqlPreview(string sql, string title = "SQL preview — changes to save");
}
