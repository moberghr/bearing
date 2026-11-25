using System;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;

namespace Bearing.Sessions;

/// <summary>
/// The one retry a <b>user-initiated</b> connect gets after its login is refused: refresh the credential —
/// which, for a stored-password connection with a prompt, asks for the password (saved to the keychain once a
/// connect with it worked, <see cref="CredentialResolver.SaveIfWorkedAsync"/>) — and try once more.
///
/// <para>Shared by every user-initiated route so none of them carries its own copy: the Connect button and
/// the sidebar tree's expand (<see cref="IConnectionSessionManager.ConnectForUserAsync"/>,
/// <see cref="ISchemaBrowser.GetDatabasesForUserAsync"/> / <see cref="ISchemaBrowser.GetObjectsForUserAsync"/>),
/// and the query run, whose retry loop stays in the App only because an authentication failure can also arrive
/// in a result there — its decision is <see cref="Refresh"/>, the same as here.</para>
///
/// <para><b>Background work never comes through here</b> and so never prompts on a failure: schema warm-up,
/// the activity poll, write-confirmation counts (TryGet only) and sweeps call the plain methods.</para>
///
/// <para>One prompt per failure: the credential's generation is read before the attempt and only refreshed if
/// nobody else did meanwhile (<see cref="CredentialResolver.InvalidateIfUnchanged"/>), and concurrent resolves
/// share one prompt. Two callers failing together end in one prompt and two retries with its answer.</para>
/// </summary>
public static class AuthRetry
{
    /// <summary>Run <paramref name="attempt"/>; if it fails with an authentication failure the resolver can do
    /// something about, refresh and run it once more (<paramref name="beforeRetry"/> first, to drop whatever the
    /// failed attempt cached). Any other failure, and the retry's own failure, propagate unchanged.</summary>
    public static async Task<T> OnceAsync<T>(
        CredentialResolver? credentials, IProviderRegistry providers, ConnectionInfo info,
        Func<Task<T>> attempt, Func<Task>? beforeRetry, CancellationToken ct)
    {
        if (credentials is null) return await attempt();
        var generation = credentials.Generation(info.Id);
        try
        {
            return await attempt();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OperationCanceledException
                                   && IsAuthentication(providers, info, ex) && credentials.CanRefresh(info))
        {
            Refresh(credentials, info, generation);
            if (beforeRetry is not null) await beforeRetry();
            return await attempt();
        }
    }

    /// <summary>Refresh after a refused login — unless someone already did since
    /// <paramref name="generation"/> was read, in which case their new credential is the one to retry with.</summary>
    public static void Refresh(CredentialResolver credentials, ConnectionInfo info, int generation)
        => credentials.InvalidateIfUnchanged(info.Id, generation);

    /// <summary>Whether the engine calls this failure an authentication one. An engine this build doesn't have,
    /// or a classifier that throws, is "no" — a retry must never be the reason a failure turns into another.</summary>
    public static bool IsAuthentication(IProviderRegistry providers, ConnectionInfo info, Exception ex)
    {
        try { return providers.Get(info.ProviderId).ClassifyException(ex) == DbErrorKind.Authentication; }
        catch { return false; }
    }
}
