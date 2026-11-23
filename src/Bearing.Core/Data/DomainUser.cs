namespace Bearing.Core.Data;

/// <summary>
/// A domain (Active Directory) login as typed into the User box, split into what NTLM is handed
/// (<see cref="CredentialKind.DomainPassword"/>).
/// <para>
/// Two spellings are accepted, and they are passed on differently on purpose:
/// <list type="bullet">
/// <item><c>DOMAIN\user</c> — the down-level name: the domain and the account name, separately.</item>
/// <item><c>user@domain</c> — a user principal name, passed <b>whole</b> with no domain. It is not split,
/// because the part before the <c>@</c> is not promised to be the account name; the domain controller
/// resolves a UPN itself.</item>
/// </list>
/// Anything else is not a domain login and is refused rather than guessed at: a bare <c>user</c> would
/// authenticate against whatever domain the server defaults to.
/// </para>
/// </summary>
/// <param name="Domain">The NetBIOS or DNS domain, or empty for a UPN.</param>
/// <param name="User">The account name, or the whole UPN.</param>
public readonly record struct DomainUser(string Domain, string User)
{
    public static bool TryParse(string? text, out DomainUser login)
    {
        login = default;
        var value = (text ?? "").Trim();

        var slash = value.IndexOf('\\');
        if (slash >= 0)
        {
            // Exactly one separator with something on each side; DOMAIN\a\b names nothing.
            if (slash == 0 || slash == value.Length - 1 || value.IndexOf('\\', slash + 1) >= 0) return false;
            login = new DomainUser(value[..slash], value[(slash + 1)..]);
            return true;
        }

        var at = value.LastIndexOf('@');
        if (at <= 0 || at == value.Length - 1) return false;
        login = new DomainUser("", value);
        return true;
    }
}
