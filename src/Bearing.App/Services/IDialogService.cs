using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

public interface IDialogService
{
    /// <summary>Confirm a risky write/DDL batch against a guarded connection. True = proceed.
    /// Implementations with no window (headless/tests) proceed silently.</summary>
    Task<bool> ConfirmWriteAsync(ConnectionInfo connection, IReadOnlyList<string> verbs);

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
    /// null if cancelled. <paramref name="test"/> backs the dialog's Test button.</summary>
    Task<ConnectionDialogResult?> ShowConnectionDialogAsync(
        ConnectionInfo? existing,
        string? existingPassword,
        Func<ConnectionInfo, string?, CancellationToken, Task<bool>> test,
        bool secretStorageSecure);

    /// <summary>Prompt for a single line of text (rename, new folder/script, project name). Null if cancelled.</summary>
    Task<string?> ShowTextPromptAsync(string prompt, string initial = "");

    /// <summary>Pick an existing folder (project open/new). Returns its local path, or null if cancelled.</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>Pick an existing .sql file to open. <paramref name="startDir"/> seeds the initial location.
    /// Returns the local path, or null if cancelled.</summary>
    Task<string?> PickOpenScriptAsync(string? startDir);

    /// <summary>Pick a destination for saving a .sql file. Returns the local path, or null if cancelled.</summary>
    Task<string?> PickSaveScriptAsync(string suggestedName, string? startDir);

    /// <summary>Show SQL in a read-only, monospace preview window (non-modal; selectable to copy).</summary>
    void ShowSqlPreview(string sql, string title = "SQL preview — changes to save");
}
