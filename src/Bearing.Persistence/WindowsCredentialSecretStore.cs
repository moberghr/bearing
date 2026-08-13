using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Bearing.Core.Workspace;

namespace Bearing.Persistence;

/// <summary>
/// OS credential store on Windows: the Credential Manager (<c>CredWrite</c>/<c>CredRead</c>/
/// <c>CredDelete</c>). This is the closest analogue to libsecret — the blob is encrypted by the OS under the
/// current user's logon credentials, no file of ours is involved, and the entry is visible and revocable in
/// Control Panel ▸ Credential Manager ▸ Windows Credentials.
/// <para>
/// Keyed by target name <c>&lt;app&gt;:connection:&lt;guid&gt;</c>, mirroring the {app, connection}
/// attribute pair the Linux store uses — <see cref="BearingPaths.AppDirName"/> carries the
/// <c>BEARING_PROFILE</c> isolation, so a dev profile never reads the installed app's secrets.
/// </para>
/// <para>
/// Persisted as <c>CRED_PERSIST_LOCAL_MACHINE</c>: the credential outlives the logon session but never
/// roams to a domain profile, matching where the rest of Bearing's local state lives (<c>%LOCALAPPDATA%</c>,
/// see <see cref="BearingPaths"/>).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialSecretStore : ISecretStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    /// <summary>CRED_MAX_CREDENTIAL_BLOB_SIZE — 5 × 512 bytes, i.e. 1,280 UTF-16 characters.</summary>
    private const int MaxBlobBytes = 5 * 512;

    public bool IsSecure => true;

    /// <summary>Always: the credential store is where a password belongs, so there's nothing to opt into.</summary>
    public bool CanStore => true;

    /// <summary>The Credential Manager key. Guid formatting is invariant, so this is stable across locales.</summary>
    private static string TargetFor(Guid id) => $"{BearingPaths.AppDirName}:connection:{id}";

    public Task SetPasswordAsync(Guid connectionId, string password, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // UTF-16 is what Windows itself puts in a CredentialBlob, so a password stored here reads correctly
        // in any other Windows tooling that inspects it.
        var blob = Encoding.Unicode.GetBytes(password);
        if (blob.Length > MaxBlobBytes)
            throw new InvalidOperationException(
                $"The password is too long for the Windows Credential Manager: {blob.Length} bytes, "
                + $"and the limit is {MaxBlobBytes} ({MaxBlobBytes / 2} characters).");

        var target = Marshal.StringToCoTaskMemUni(TargetFor(connectionId));
        var user = Marshal.StringToCoTaskMemUni(connectionId.ToString());
        var comment = Marshal.StringToCoTaskMemUni($"Bearing connection {connectionId}");
        var blobPtr = Marshal.AllocCoTaskMem(Math.Max(blob.Length, 1));
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                Comment = comment,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = user,
            };
            // CredWrite replaces an existing credential with the same target+type, so rotating a password
            // needs no delete first.
            if (!CredWriteW(ref cred, 0))
                throw new InvalidOperationException($"CredWrite failed: {Describe(Marshal.GetLastPInvokeError())}");
        }
        finally
        {
            // Don't leave the password sitting in unmanaged memory after the OS has taken its copy.
            Zero(blobPtr, blob.Length);
            Array.Clear(blob);
            Marshal.FreeCoTaskMem(blobPtr);
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(user);
            Marshal.FreeCoTaskMem(comment);
        }

        return Task.CompletedTask;
    }

    /// <summary>Null when nothing is stored for this connection. An unexpected failure throws rather than
    /// reading as "no password": the store is probed at startup (see <see cref="SecretStoreFactory"/>), so a
    /// Credential Manager that can't be used at all never gets attached in the first place, and a failure
    /// here means something the user should hear about.</summary>
    public Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!CredReadW(TargetFor(connectionId), CRED_TYPE_GENERIC, 0, out var handle))
        {
            var err = Marshal.GetLastPInvokeError();
            if (err == ERROR_NOT_FOUND) return Task.FromResult<string?>(null);
            throw new InvalidOperationException($"CredRead failed: {Describe(err)}");
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
                return Task.FromResult<string?>(string.Empty);

            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            var value = Encoding.Unicode.GetString(bytes);
            Array.Clear(bytes);
            return Task.FromResult<string?>(value);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public Task DeleteAsync(Guid connectionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (CredDeleteW(TargetFor(connectionId), CRED_TYPE_GENERIC, 0)) return Task.CompletedTask;

        // Unlike the CLI-backed stores, this API distinguishes the two cases honestly: "there was nothing to
        // delete" is a successful outcome for a caller clearing a credential, anything else is not.
        var err = Marshal.GetLastPInvokeError();
        if (err == ERROR_NOT_FOUND) return Task.CompletedTask;
        throw new InvalidOperationException($"CredDelete failed: {Describe(err)}");
    }

    private static void Zero(IntPtr ptr, int length)
    {
        for (var i = 0; i < length; i++) Marshal.WriteByte(ptr, i, 0);
    }

    /// <summary>Win32 code plus the OS's own text — the code is what makes an unfamiliar failure searchable.</summary>
    private static string Describe(int error) => $"{new Win32Exception(error).Message} ({error})";

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;        // LPWSTR
        public IntPtr Comment;           // LPWSTR
        public long LastWritten;         // FILETIME — output only
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;       // LPWSTR
        public IntPtr UserName;          // LPWSTR
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string targetName, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string targetName, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
}
