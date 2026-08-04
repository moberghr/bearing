using System;
using System.Threading.Tasks;
using Bearing.Persistence;

namespace Bearing.App;

/// <summary>
/// Central sink for unexpected errors. Always logs to <see cref="CrashLog"/>, then (if wired) surfaces
/// the error to the user. Wired once at startup via <see cref="Surface"/>; safe to call from any thread
/// and guaranteed not to throw.
/// </summary>
public static class CrashReporter
{
    /// <summary>Set by the app to present an error to the user. The handler marshals to the UI thread itself.</summary>
    public static Action<string, Exception>? Surface { get; set; }

    public static void Report(string context, Exception ex)
    {
        CrashLog.Write(context, ex);
        try { Surface?.Invoke(context, ex); }
        catch { /* reporting must never throw */ }
    }

    /// <summary>
    /// Observe a fire-and-forget command/task so a fault is logged and surfaced rather than silently
    /// swallowed (or left unobserved). Cancellation is expected and ignored.
    /// </summary>
    public static async void Observe(ValueTask work, string context)
    {
        try { await work; }
        catch (OperationCanceledException) { /* user-initiated */ }
        catch (Exception ex) { Report(context, ex); }
    }

    /// <inheritdoc cref="Observe(ValueTask, string)"/>
    public static async void Observe(Task work, string context)
    {
        try { await work; }
        catch (OperationCanceledException) { /* user-initiated */ }
        catch (Exception ex) { Report(context, ex); }
    }
}
