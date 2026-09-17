using System;
using System.Globalization;
using Bearing.Core.Data;

namespace Bearing.App.Services;

/// <summary>Which of the two things is being done to a backend (#101).</summary>
public enum BackendActionKind
{
    /// <summary>Stop the statement; the session survives and its client sees the query fail as cancelled.</summary>
    Cancel,

    /// <summary>End the session outright, dropping its connection.</summary>
    Terminate,
}

/// <summary>
/// A pending Cancel / Terminate, and every word the confirmation shows (#101). Pure, so the wording is
/// testable without a window — the division <see cref="WriteConfirmation"/> already draws with its dialog.
/// <para>
/// Both actions confirm, for every session including our own. One rule, one code path, and the cost is a
/// click on the common case: the alternative — prompting only for other people's sessions — makes the
/// dangerous act the one with less friction whenever Bearing's own `application_name` is wrong about who
/// owns a backend.
/// </para>
/// </summary>
public sealed record BackendAction(BackendActionKind Kind, ConnectionInfo Connection, BackendActivity Backend)
{
    public string Title => Kind == BackendActionKind.Cancel ? "Cancel statement" : "Terminate session";

    public string Heading => Kind == BackendActionKind.Cancel
        ? $"Cancel the statement on backend {Backend.Pid}?"
        : $"Terminate backend {Backend.Pid}?";

    /// <summary>What the action does, and — for terminate — what it costs. Said plainly rather than as a
    /// warning banner: the difference between the two is the whole decision.</summary>
    public string Summary => Kind == BackendActionKind.Cancel
        ? "The statement stops and the session stays open. Whoever ran it sees their query fail as cancelled."
        : "The session is disconnected, not just interrupted. Any transaction it has open is rolled back, and "
          + "whoever is using it loses their connection.";

    /// <summary>
    /// Who this backend belongs to, in one line: the role, the database, and what it says it is.
    /// <para>
    /// This is the line that answers "am I about to kill the right one", so it names the role rather than
    /// leaving the pid to stand alone — a pid is a number with nothing in it to recognise.
    /// </para>
    /// </summary>
    public string Target
    {
        get
        {
            var who = string.IsNullOrWhiteSpace(Backend.User) ? "an unnamed role" : Backend.User!;
            var where = string.IsNullOrWhiteSpace(Backend.Database) ? Connection.Name : Backend.Database!;
            var what = string.IsNullOrWhiteSpace(Backend.Application) ? null : Backend.Application;
            var line = $"{who} on {where}";
            if (what is not null) line += $" · {what}";
            return Backend.IsOurs ? $"{line} — opened by Bearing" : line;
        }
    }

    /// <summary>
    /// How long this backend has been doing whatever it is doing — "Running for 3 min 4 s" when a statement
    /// is executing, and "Idle in transaction for 41 min" when one is not.
    /// <para>
    /// Two sentences rather than one, because they are two different facts and the confirmation is where
    /// getting them mixed up costs something: a row that has held a transaction open for 41 minutes and a row
    /// that has been executing for 41 minutes call for different decisions, and the dialog used to say
    /// "Running for" about both (<see cref="BackendActivity.RunningFor"/>).
    /// </para>
    /// <para>
    /// Null when the role could see neither, which is absence rather than zero: a session with no elapsed
    /// statement is not a statement of no length.
    /// </para>
    /// </summary>
    public string? Running
    {
        get
        {
            if (Backend.RunningFor is { } running) return "Running for " + FormatElapsed(running);
            if (Backend.StateFor is not { } waiting) return null;
            var state = Backend.State;
            if (string.IsNullOrWhiteSpace(state)) return null;
            // "idle in transaction" as the server spells it, sentence-cased — the state is the subject of
            // this line, and lower-casing the server's own word would invent a second vocabulary for it.
            return char.ToUpperInvariant(state[0]) + state[1..] + " for " + FormatElapsed(waiting);
        }
    }

    /// <summary>The statement itself, or null when the role could not see it or there is none.</summary>
    public string? Query => string.IsNullOrWhiteSpace(Backend.Query) ? null : Backend.Query;

    /// <summary>Named for what it does rather than "OK", so the button and the heading cannot drift apart.</summary>
    public string ConfirmLabel => Kind == BackendActionKind.Cancel ? "Cancel statement" : "Terminate";

    /// <summary>The status line after the server answered yes.</summary>
    public string DoneText => Kind == BackendActionKind.Cancel
        ? $"Asked backend {Backend.Pid} to cancel."
        : $"Terminated backend {Backend.Pid}.";

    /// <summary>
    /// The status line after the server answered <em>no</em>. Not an error: by far the commonest reason is
    /// that the backend finished on its own between the poll that listed it and the click, which is the
    /// system working.
    /// </summary>
    public string RefusedText =>
        $"Backend {Backend.Pid} is no longer there, or this role may not signal it.";

    /// <summary>"2.1 s" / "3 min 4 s" / "1 h 12 min" — the shape a duration has to read in to be glanced at
    /// beside a pid.</summary>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalSeconds < 10)
            return elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        if (elapsed.TotalMinutes < 1)
            return ((int)elapsed.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
        if (elapsed.TotalHours < 1)
            return $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s";
        return $"{(int)elapsed.TotalHours} h {elapsed.Minutes} min";
    }
}
