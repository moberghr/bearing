using System.Globalization;
using Bearing.Core.Data;
using Bearing.Sessions;

namespace Bearing.App.Connections;

/// <summary>Where a connection dialog's Test stands.</summary>
public enum ConnectionTestPhase
{
    NotTested,
    Testing,
    Succeeded,
    Failed,

    /// <summary>The user stopped a running test. Not a failure: nothing was learned about the server.</summary>
    Stopped,
}

/// <summary>
/// What the connection dialog's status row says for one moment of a Test: the wording, kept apart from the
/// control that draws it so it can be unit-tested without a window (§2.5).
/// <para>
/// The endpoint is spelled by <see cref="ConnectionEndpoint"/>, like every other surface that names a
/// server, so a named SQL Server instance drops the port here too. Nothing in this text is read from the
/// server: it describes the settings that were tested and how long the attempt took.
/// </para>
/// </summary>
/// <param name="Phase">Which state the row is in; decides the beacon and the colours.</param>
/// <param name="Headline">The coloured first line.</param>
/// <param name="Detail">The dim second line, present in every state so the row keeps its height. For a
/// failure, the (already redacted) error.</param>
/// <param name="Aside">Right-aligned figure: the running or final duration, or empty.</param>
public sealed record ConnectionTestStatus(
    ConnectionTestPhase Phase, string Headline, string Detail = "", string Aside = "")
{
    public static ConnectionTestStatus NotTested { get; } = new(ConnectionTestPhase.NotTested, "Not tested", "Press Test to try these settings.");

    public static ConnectionTestStatus Stopped { get; } = new(ConnectionTestPhase.Stopped, "Test stopped", "Stopped before an answer came back.");

    public static ConnectionTestStatus Testing(ConnectionInfo info, TimeSpan elapsed)
        => new(ConnectionTestPhase.Testing,
            $"Testing {ConnectionEndpoint.HostPort(info)}…",
            ConnectionEndpoint.Full(info),
            Seconds(elapsed));

    public static ConnectionTestStatus Succeeded(ConnectionInfo info, TimeSpan elapsed)
        => new(ConnectionTestPhase.Succeeded, "Connected", ConnectionEndpoint.Full(info), Duration(elapsed));

    /// <summary>A failed test. <paramref name="error"/> must already be redacted
    /// (<see cref="SafeErrorText"/>): the dialog holds an unsaved password. A test that reported failure
    /// without throwing has no cause to show, and none is invented.</summary>
    public static ConnectionTestStatus Failed(string? error)
        => new(ConnectionTestPhase.Failed, "Could not connect",
            string.IsNullOrWhiteSpace(error) ? "No error was reported." : error.Trim());

    /// <summary>The beacon for this phase, or null where there is no session state to show.</summary>
    public ConnectionState? Beacon => Phase switch
    {
        ConnectionTestPhase.Testing => ConnectionState.Connecting,
        ConnectionTestPhase.Succeeded => ConnectionState.Connected,
        ConnectionTestPhase.Failed => ConnectionState.Disconnected,
        _ => null,
    };

    /// <summary>Whole seconds for the running counter, so it does not flicker.</summary>
    private static string Seconds(TimeSpan elapsed)
        => string.Create(CultureInfo.InvariantCulture, $"{(long)elapsed.TotalSeconds} s");

    /// <summary>How long the whole test took: milliseconds under a second, then one decimal of seconds.
    /// It covers everything Test did (credential lookup included), so it is not labelled a ping.</summary>
    private static string Duration(TimeSpan elapsed)
        => elapsed.TotalMilliseconds < 1000
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (long)elapsed.TotalMilliseconds)} ms")
            : string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalSeconds:0.0} s");
}
