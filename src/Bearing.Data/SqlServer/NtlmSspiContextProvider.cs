using System.Buffers;
using System.Net;
using System.Net.Security;
using Microsoft.Data.SqlClient;

namespace Bearing.Data.SqlServer;

/// <summary>
/// Answers SQL Server's integrated-authentication handshake with NTLM for an explicit domain account
/// (<see cref="Core.Data.CredentialKind.DomainPassword"/>), instead of the identity of the process. This is
/// what lets a machine that is not joined to the domain — a Mac — log in as <c>DOMAIN\user</c> with a
/// password and no Kerberos ticket.
/// <para>
/// The password lives here, on the connection object's side, and never in the connection string, where the
/// driver, its exceptions and any builder round-trip could quote it (§1.1).
/// </para>
/// <para>
/// <b>One instance per factory, not per connection.</b> SqlClient makes the provider part of the pool's
/// identity and compares it by reference, so a fresh instance per <c>SqlConnection</c> would be a fresh
/// pool per open. Sharing it also keeps two connections with different domain users out of each other's
/// pool, which the connection string alone (it names no user) would not. The price is that the driver then
/// runs every physical login of the pool through this one object, so a handshake's state is started anew
/// whenever the server's side of the conversation is empty, and the factory opens connections one at a
/// time (<see cref="SqlServerConnectionFactory.OpenConnectionAsync"/>) so two handshakes never interleave.
/// </para>
/// </summary>
internal sealed class NtlmSspiContextProvider : SspiContextProvider
{
    private readonly NetworkCredential _credential;
    private readonly object _gate = new();
    private NegotiateAuthentication? _handshake;

    public NtlmSspiContextProvider(NetworkCredential credential) => _credential = credential;

    protected override bool GenerateContext(
        ReadOnlySpan<byte> incomingBlob, IBufferWriter<byte> outgoingBlobWriter, SspiAuthenticationParameters authParams)
    {
        lock (_gate)
        {
            // Nothing from the server yet means a new login is starting; whatever an earlier one left
            // behind (a handshake a dropped socket never finished) belongs to a connection that is gone.
            if (incomingBlob.IsEmpty || _handshake is null)
            {
                _handshake?.Dispose();
                _handshake = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
                {
                    Package = "NTLM",
                    Credential = _credential,
                    TargetName = authParams.Resource,
                });
            }

            var blob = _handshake.GetOutgoingBlob(incomingBlob, out var status);
            var done = status == NegotiateAuthenticationStatusCode.Completed;
            if (done || status != NegotiateAuthenticationStatusCode.ContinueNeeded)
            {
                _handshake.Dispose();
                _handshake = null;
            }

            // Reported as "could not generate" by the driver. A wrong password is not this: NTLM completes
            // on the client either way and the server refuses the login (18456 / 18452).
            if (!done && status != NegotiateAuthenticationStatusCode.ContinueNeeded) return false;

            if (blob is { Length: > 0 }) outgoingBlobWriter.Write(blob);
            return true;
        }
    }
}
