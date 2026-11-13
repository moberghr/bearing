# Plan — Connect through an SSH tunnel

Spec: `docs/specs/2026-11-13-ssh-tunnel.md` (+ `.json`). Each batch ends with `dotnet build` and the tests
it touches; full `dotnet test` after batch 5, then again with the SSH/PG/MSSQL/MySQL containers up.

## Batch 1 — Model and pure helpers (Core + Sessions, no network)
- Core: `SshTunnel` record + `SshAuthKind`; `ConnectionInfo.SshTunnel`; `ConnectionRoute`.
- Sessions: `SshSecretId.For(Guid)`; `SshHostKey` (format `SHA256:…`, decide Match/Unknown/Changed);
  `ISshPrompt` (secret + host-key confirm).
- `ConnectionEndpoint.Full` "via ssh …".
- Tests: `SshSecretIdTests`, `SshHostKeyTests`, `ConnectionEndpointTests`.

## Batch 2 — Provider route (Core contract + Data)
- `IDbProvider.CreateConnectionFactory(info, password, route = null)`; all four providers.
- SqlClient: DataSource + `HostNameInCertificate`; Npgsql / MySqlConnector: loopback host/port and a
  verify-full callback validating chain + `info.Host` (checked against driver source first).
- Named instance + route → `ArgumentException` with the dialog's sentence (the dialog refuses earlier).
- Tests: `ConnectionRouteTests` (connection-string shape; callback refuses wrong name / broken chain).

## Batch 3 — Tunnel pool and recipe (Sessions)
- Add `SSH.NET` 2026.0.0 to `Directory.Packages.props` + `Bearing.Sessions.csproj`.
- `ISshTunnelOpener` + `SshNetTunnelOpener` (connect, host-key check via `SshHostKey`, auth, `ForwardedPortLocal`
  on `127.0.0.1:0`, keep-alive, `Lost` event).
- `SshCredentialResolver` (stored → cache → prompt after auth failure; no prompt in CLI).
- `SshTunnelPool` (ref-counted leases, single-flight open, `TunnelLost`).
- `ConnectionFactoryBuilder` returns the lease; `ConnectionSession` / `SchemaBrowser.Reader` dispose it after
  the factory; failure releases. `ConnectionSessionManager`: `SameConnection` includes tunnel identity;
  `TunnelLost` → `EvictConnectionAsync`. `ConnectionTester` for the dialog's Test.
- Tests: `SshTunnelPoolTests`, `SshCredentialResolverTests`, `ConnectionFactoryBuilderTunnelTests`,
  `ConnectionIdentityTunnelTests` (Sessions half).

## Batch 4 — App + CLI wiring
- `WorkspaceContext` / `App.axaml.cs`: construct opener, resolver, pool; `DialogSshPrompt` (secret dialog +
  host-key trust dialog; Cancel is default). On Trust, pin via the normal save path (no eviction).
- `ConnectionsViewModel`: `SameNetwork` includes tunnel; save/delete SSH secret under `SshSecretId`;
  `TestConnectionAsync` → `ConnectionTester`.
- `ConnectionClipboard.Entry` tunnel fields (+ reflection test).
- CLI `Program.cs`: pool with `prompt: null`.
- Tests: `ConnectionIdentityTunnelTests` (App half), `ConnectionClipboardTests`.

## Batch 5 — Dialog
- `SshTunnelForm` (fields, validation: host/user required, key path required for PrivateKey, named instance
  refused); SSH tab in `ConnectionDialog.axaml`; thin code-behind wiring; Browse… via storage provider;
  Forget host key. Hidden for the demo provider.
- Tests: `Ui/ConnectionDialogSshTests` (tab toggles fields, Forget clears pin, result carries tunnel).

## Batch 6 — Live tests and docs
- `build/test-ssh.sh` (openssh-server container joined to the DB containers' network, password + key user).
- `tests/Shared/SshTestServer.cs` (`BEARING_TEST_SSH_*`), `LiveSshTunnelTests` over pg / mssql / mariadb+mysql.
- `.claude/rules/security.md` §1.12 (SSH tunnel rules), `CLAUDE.md` project profile line.
