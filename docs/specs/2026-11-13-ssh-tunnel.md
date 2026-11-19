# Spec — Connect through an SSH tunnel

Date: 2026-11-13 · Branch: `ssh-tunnel` · Scope: **feature, multi-project (Core record + Sessions + Data
providers + App dialog + CLI wiring), new NuGet package**

> **Revision 2 (2026-11-13, at the approval gate): minimal scope.** The user needs this for MariaDB only,
> "minimal effort, just make it work". What ships: `ConnectionInfo.SshTunnel`; SSH.NET local forward opened
> inside the connect recipe, the driver given `127.0.0.1:<port>` (no provider change — so it works for every
> engine, but only MariaDB is exercised); password or private key (+ passphrase) from the keychain under a
> derived id, prompted when missing; host key **accept-new** (OpenSSH `StrictHostKeyChecking=accept-new`):
> first key pinned into the connection, a changed key refused, CLI refuses an unpinned key; one tunnel per
> pool (no shared pool, no lost-tunnel eviction); SSH tab in the dialog with Forget host key. **Dropped from
> revision 1:** `ConnectionRoute` / TLS server-name override (Verify Full through a tunnel checks
> `127.0.0.1` and will fail — use Require), `SshTunnelPool`, `TunnelLost`, trust dialog, live sshd tests,
> named-instance/Integrated handling. Sections below are revision 1, kept for when this grows.

> **Revision 3 (2026-11-13, after the user's first try):** password and key are separate logins
> (`SshTunnel.Auth`, "Login with" in the dialog) with their own boxes — SSH password, or key file + passphrase —
> saved to the keychain on Save and used by Test; a blank key file means ssh's default keys. The connect-time
> prompt stays as the fallback for a missing or refused secret.

Decisions settled with the user (2026-11-13):
- **Engine:** SSH.NET (`SSH.NET` 2026.0.0, targets net10.0) in-process — no `ssh` child process.
  Consequence: no ssh-agent, no `~/.ssh/config`, no `ProxyJump` in v1 (stated, not hidden).
- **Host key:** trust-on-first-use; the accepted SHA256 fingerprint is pinned in the connection and travels
  with the project. A changed key is refused. The CLI never trusts a new key (nobody to ask).

## Goal

A connection can reach its database server through an SSH bastion: Bearing opens an SSH session to the
bastion, forwards a loopback port to `Host:Port` as seen from the bastion, and the driver connects to that
loopback port. Works for every engine (PostgreSQL, SQL Server, MySQL/MariaDB) and in both hosts (app and
`bearing` CLI), because it lives in the one connect recipe (§2.6).

## Behaviour

1. **Model (Core, dependency-free record).** `ConnectionInfo.SshTunnel : SshTunnel?` — null = direct (every
   existing project unchanged). `SshTunnel` record:
   - `Host` (required), `Port` (default 22), `User` (required)
   - `Auth : SshAuthKind { Password, PrivateKey }`
   - `PrivateKeyPath : string?` — a file path, `~` expanded at connect time. The key itself is never copied
     into the project.
   - `HostKeyFingerprint : string?` — `SHA256:<base64>` as OpenSSH prints it; null = not yet trusted.
   - `ConnectionInfo.Host`/`Port` keep meaning "the database server **as the bastion sees it**" (often
     `localhost` / a private address). The dialog says so.
2. **Secrets (§1.1).** The SSH password, or the private key's passphrase, goes to `ISecretStore` under a
   **derived id** — `SshSecretId.For(connectionId)`, a deterministic name-based Guid — so the `ISecretStore`
   contract is unchanged and the three OS stores need no change. Same posture as the DB password: no store →
   `NoSecretStore` refuses, the secret is prompted and held in memory for the session; no on-disk fallback.
   Deleting a connection deletes both secrets. An unencrypted key needs no secret.
3. **Credential resolution.** A new `SshCredentialResolver` (Sessions) mirrors `CredentialResolver`'s
   stored-password branch: stored secret → in-memory cache → on an SSH *authentication* failure, prompt once
   (`ISshPrompt.RequestSecretAsync`) and retry. The CLI has no prompt: an SSH secret missing from the
   keychain fails with a sentence saying so.
4. **Host key verification (§1.4 — never disabled).** SSH.NET's `HostKeyReceived` compares the server's
   SHA256 fingerprint with `HostKeyFingerprint`:
   - equal → accept;
   - pin is null → `ISshPrompt.ConfirmHostKeyAsync(info, keyType, fingerprint)`; on Trust the app pins it
     (writes the connection through the normal save path — no pool rebuild, see 7) and the connect
     continues; on Cancel the connect fails "host key not trusted". CLI: no prompt → refused with "open the
     connection in Bearing once to trust its SSH host key";
   - different → **refused**, message shows both fingerprints and says the key *changed* (never a guessed
     cause). Replacing the pin is an explicit action in the dialog's SSH tab ("Forget host key"); there is
     no "trust anyway" button on the failure.
5. **Tunnel lifetime — one SSH session per connection, shared.** `SshTunnelPool` (Sessions), keyed by
   connection id + tunnel identity (bastion host/port/user/auth/key path + target host/port), ref-counted:
   every DB pool (per database, §9.4a) and every `SchemaBrowser` reader of that connection leases the same
   SSH client and the same `ForwardedPortLocal` bound to `127.0.0.1:0` (ephemeral, loopback only — never
   `0.0.0.0`). Last lease released → port stopped, client disposed. Keep-alive 30 s.
   - A dropped SSH session (`ErrorOccurred` / keep-alive failure) marks the tunnel dead and raises
     `TunnelLost(connectionId)`; `ConnectionSessionManager` reacts with `EvictConnectionAsync` (so the beacon
     goes Disconnected, §9.4, and open manual-commit transactions follow the existing evict path, §1.10).
     The next connect builds a fresh tunnel. No silent reconnect under a held transaction.
6. **Connect recipe.** `ConnectionFactoryBuilder.BuildAsync` (the only recipe, §1.9): if `SshTunnel` is set,
   lease a tunnel, then call the provider with a **route override** — the provider dials
   `127.0.0.1:<local port>` but keeps `info.Host` as the TLS server name. The lease is returned alongside the
   factory; `ConnectionSession` and `SchemaBrowser.Reader` dispose it **after** the factory. A failed
   connect releases it. The dialog's Test button goes through a new public Sessions helper
   (`ConnectionTester`) instead of calling `CreateConnectionFactory` itself, so Test uses the same recipe.
7. **TLS through the tunnel.** `IDbProvider.CreateConnectionFactory(info, password, route?)` — `route` is a
   new optional `ConnectionRoute(string Host, int Port)` in Core. With a route:
   - SQL Server: `DataSource = 127.0.0.1,<port>`, `HostNameInCertificate = info.Host` when the mode verifies.
   - PostgreSQL: `Host=127.0.0.1;Port=<port>`; Verify Full validates the chain **and** the name against
     `info.Host` via the data source's certificate-validation callback. Verify CA / Require / Prefer unchanged.
   - MySQL/MariaDB: same as Postgres via MySqlConnector's validation callback.
   - Named SQL Server instances (`HOST\INST`, resolved by SQL Browser over UDP) cannot be tunnelled: the
     dialog refuses the combination with a sentence saying why (use the instance's port instead).
   - Demo provider: SSH tab hidden (no socket).
8. **Pool identity.** `SshTunnel` (all fields except `HostKeyFingerprint`) joins `SameConnection` (Sessions)
   and `SameNetwork` (App) — a different bastion is a different route to the server. Pinning a fingerprint
   changes nothing live.
9. **Display.** `ConnectionEndpoint.Full` / status tip append ` via ssh user@bastion:port`; the tree row's
   `HostPort` stays the DB endpoint. Connect-failure messages say which leg failed: "SSH to bastion:22
   failed: …" vs the existing DB failure text. SSH errors pass through `SafeErrorText` — never a secret.
10. **Dialog.** New **SSH** tab in `ConnectionDialog`: "Connect through SSH tunnel" checkbox; bastion host,
    port, user; auth radio (Password | Private key); key path with Browse…; one secret box labelled
    "Password" or "Key passphrase (optional)"; pinned fingerprint shown read-only with "Forget host key";
    note: "Host and port on the Connection tab are as seen from the SSH server." Fields live in a small
    VM/helper (`SshTunnelForm`), not more dialog code-behind (§0.4 spirit, §2.3).
11. **Clipboard / import.** `ConnectionClipboard.Entry` carries the tunnel (host, port, user, auth, key path
    **and** the pinned fingerprint — a pin is a restriction, so dropping it would be a silent downgrade);
    never a secret. The reflection test is updated.
12. **CLI.** Works unchanged through `Sessions`; `ExternalAccessPolicy.UnavailableReason` does not change
    (an SSH tunnel is not a credential kind). Missing secret / untrusted key → refusal sentences above.
13. **Logging.** Nothing about SSH is written to the query log; secrets never reach any log or message.

## Out of scope (v1) — stated in the dialog/docs, not silently missing

ssh-agent / Pageant, `~/.ssh/config` and `known_hosts` import, jump-host chains (`ProxyJump`), keyboard-
interactive / OTP, SSH certificates, dynamic (SOCKS) forwarding. All are additive later (agent would be a
third `SshAuthKind`).

## Open questions for the gate

- **Integrated (Windows/Kerberos) auth over a tunnel** on SQL Server: the SPN would be built from
  `127.0.0.1`. Plan: set `ServerSPN = MSSQLSvc/<info.Host>:<port>` with a route; untestable here without a
  domain. Alternative: refuse Integrated + SSH in v1 (a scope cut — needs your yes).

## Risks

- TLS name validation callbacks (Npgsql / MySqlConnector) must keep full chain validation — verified against
  driver source before writing them (source-driven), and pinned by tests that a wrong name is refused.
- Azure SQL *Redirect* connection policy sends the client to another port → fails through a tunnel; the
  failure text cannot know this. Documented, not detected.
- SSH.NET adds `BouncyCastle.Cryptography` transitively (central transitive pinning is on).
- Live tests need an `sshd` container: new `build/test-ssh.sh` (linuxserver/openssh-server, forwarding
  enabled, on the docker network of `squirrel-pg-test`); tests skip without it (§4.2 — a skip is not a pass).

## Test manifest (summary)

Pure: `SshSecretId` determinism; fingerprint formatting/compare; `SshTunnelPool` ref-count/lost/teardown with
a fake opener; `ConnectionFactoryBuilder` passes the route and releases on failure; `SameConnection` /
`SameNetwork` / `SamePool` with tunnel changes (fingerprint excluded); `ConnectionEndpoint` text; clipboard
round trip; secret deleted with connection; resolver prompts only after auth failure; host-key decision table
(match / unknown+trust / unknown+cancel / unknown+no prompt / mismatch). Provider: route → connection string
(SqlClient `HostNameInCertificate`, Npgsql/MySQL host+port). Live (skippable, `SshTestServer`): Postgres,
SQL Server, MariaDB+MySQL through the bastion; wrong pin refused; wrong password → auth error; tunnel
shared by two databases' pools; Verify Full through tunnel against `info.Host`. UI (headless): SSH tab
toggles fields, Forget clears the pin. Visuals still need your eyeball QA (§0.2).
