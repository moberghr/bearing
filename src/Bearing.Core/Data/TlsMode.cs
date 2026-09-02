namespace Bearing.Core.Data;

/// <summary>
/// What the client demands of the transport to a database server. Maps onto Postgres's <c>sslmode</c>, and
/// deliberately keeps its names: this is the setting DBAs already know, and inventing softer words for it
/// would hide which of the two separate things — encryption and identity — a mode actually gets you.
/// <para>
/// <see cref="Prefer"/> is first so that a missing value in an older project file deserializes to it, which
/// is also the driver's own default and therefore the behaviour every existing connection already has.
/// </para>
/// </summary>
public enum TlsMode
{
    /// <summary>
    /// Try TLS, and fall back to plaintext if the server declines. Encrypts nothing it is not given, verifies
    /// nothing at all — and never says which of the two happened, which is what makes it the dangerous
    /// default rather than a lenient one.
    /// </summary>
    Prefer,

    /// <summary>
    /// Refuse to connect unencrypted. Stops an eavesdropper, and stops nobody from impersonating the server:
    /// the certificate is accepted without being checked, so a man in the middle presenting any certificate
    /// at all still gets a connection.
    /// </summary>
    Require,

    /// <summary>Encrypt, and check the certificate chains to a trusted authority — but not that it was issued
    /// for this host, so a valid certificate for another host is accepted.</summary>
    VerifyCa,

    /// <summary>Encrypt, check the chain, and check the certificate was issued for the host being connected
    /// to. The only mode that answers "am I talking to the server I think I am".</summary>
    VerifyFull,

    /// <summary>Never encrypt. Everything, credentials included, crosses the network in the clear.</summary>
    Disable,
}
