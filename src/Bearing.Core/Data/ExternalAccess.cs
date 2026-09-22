namespace Bearing.Core.Data;

/// <summary>
/// What a host <i>outside</i> Bearing may do with this connection — a local MCP client, an AI agent, any
/// tool that is not the window in front of the user. Default <see cref="None"/>, so a connection that
/// predates the setting, or one nobody thought about, is not exposed.
/// <para>
/// An enum rather than a bool because the question is <i>how much</i> access, not whether: see
/// <see cref="ReadWrite"/>'s absence below, which is a decision and not an omission.
/// </para>
/// </summary>
public enum ExternalAccess
{
    /// <summary>Not exposed. An external host does not list this connection and cannot run anything on it.</summary>
    None,

    /// <summary>
    /// Exposed for reads. The external session is built read-only whatever the connection is for the user
    /// (<see cref="ExternalAccessPolicy.ForExternalHost"/>), so the two are independent: a connection you
    /// write to yourself can still be handed to an agent that cannot.
    /// </summary>
    ReadOnly,

    // There is deliberately no ReadWrite member.
    //
    // Not because writes from outside are unthinkable, but because the thing that makes a write safe here
    // does not exist yet. Inside the app a guarded write is confirmed by a human looking at the row count
    // (§1.5); an external host has nobody to ask, so a ReadWrite level would have to either raise a dialog
    // on a screen the caller cannot see — blocking an unattended agent on a prompt nobody answers — or
    // drop the confirmation, which is §1.2 narrowed for the one caller least able to notice it went wrong.
    // Adding the member is the easy half; deciding that is the feature. Until then the absence says so.
}
