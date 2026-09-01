# Security Rules (§1.x)

This is a local desktop tool that stores DB credentials and executes user SQL against real servers.
Shared checklist: `.claude/references/security-checklist.md`.

## §1.1 — Secrets
- NEVER log, print, or write connection passwords to disk outside the secret store.
- Passwords go through `ISecretStore`, and the only implementations that store anything are the three OS
  credential stores (`SecretToolSecretStore`, `WindowsCredentialSecretStore`, `MacKeychainSecretStore`).
- WHEN no keychain is reachable the store is `NoSecretStore`: `SetPasswordAsync` throws
  `SecretStorageRefusedException`, reads return null, and the connection prompts and holds the password in
  memory for the session. **There is no on-disk fallback and no setting to re-enable one** (removed
  2026-08-19, with `LegacySecretFiles.Purge` deleting what the old opt-in wrote). DO NOT add a "save it
  anyway" path — an encrypted-at-rest store keyed by something the user supplies would be a new design, not
  a flag.
- The posture is surfaced, never silent: `SecretStorageSecure` in the status bar, and the connection
  dialog's amber block. That warning must say the keychain **couldn't be reached** and show the reason the
  probe reported (`ISecretStore.UnavailableReason` → `SecretStorageAdvice`) — never assert a cause nobody
  checked.

## §1.2 — Write guard (destructive SQL)
- `Bearing.Sql.WriteGuard` flags data-modifying / DDL statements (INSERT/UPDATE/DELETE/MERGE, DROP/
  TRUNCATE/ALTER, and data-modifying CTEs) on connections marked `RequireWriteConfirmation`.
- Keep the guard lexer-based and conservative. If you touch it, do not narrow what counts as risky without
  a test; the Production connection preset relies on it.

## §1.3 — Query log
- Executed SQL is logged to a local SQLite store (`SqliteQueryLog`) with retention pruning
  (`QueryLogRetentionDays`, default 180; ≤0 = keep forever). Don't add PII beyond the SQL text itself.
- Literal stripping is **opt-in** (`QueryLogRedactLiterals`, default off): on, `Bearing.Sql.SqlRedactor`
  replaces string/number/dollar-quoted literals with placeholders before the entry is written. Default off
  because a redacted statement can't be re-run from the history panel, and silently rewriting the user's own
  record of what they did isn't ours to decide. **Do not assume a log is sanitized** — check the setting.
  - Redaction happens in `Append`, at the boundary: the database row, the `Appended` event and the history
    panel then agree. A redactor that throws stores `(redaction failed)` — never the verbatim SQL, which is
    the one outcome the feature exists to prevent.
  - The redactor is lexer-based (`PgParsing`), like `WriteGuard`: a regex can't tell a quote inside a
    dollar-quoted body from one that ends a string. It is **not** anonymisation — identifiers are untouched.
  - It reaches the store as a `Func<string,string>` because `Persistence` may not reference `Sql` (§2.2).
- The log's files are narrowed to their owner on start (`LocalFilePermissions.HardenDatabase`), sidecars
  included — the `-wal` holds un-checkpointed entries, so hardening the `.sqlite` alone leaves the newest ones
  readable. A no-op on Windows (`%LOCALAPPDATA%` is already ACL'd), reported as `PlatformDefault` rather than
  as success. Best-effort: a filesystem that can't express the mode must not stop startup (§5.2).
- **Encryption at rest is deliberately not implemented.** It needs a key the user supplies, which §1.1 already
  rules is a new design rather than a flag — and a key held next to the file it encrypts protects nobody.

## §1.4 — Connections
- Transport security is `ConnectionInfo.Tls` (a `TlsMode`), not an options-bag entry (#23). `sslmode` is
  **reserved** in `PostgresConnectionString` so the bag — which travels in a shared project.json — cannot
  outrank the field, the same rule that stops a stray `Password` key beating the secret store.
- `TlsPolicy` is the pure policy: `Resolve` (the field, falling back to a legacy bag `sslmode` only while the
  field is untouched, so older projects and DBeaver imports keep working), `DefaultFor` (a **new** connection
  requires encryption unless the host is loopback), and `Advice`, which names *which* guarantee a mode is
  missing. `TlsPolicy.Default` stays `Prefer` — it exists so a project file without the field keeps the
  behaviour it had, and is deliberately not the default for anything new.
- WHEN touching this, keep encryption and identity distinct: `Require` encrypts and accepts **any**
  certificate. Do not describe it as verified, and do not collapse the modes into a bool.
- Don't hardcode credentials or disable certificate validation.
