# Security Rules (§1.x)

This is a local desktop tool that stores DB credentials and executes user SQL against real servers.
Shared checklist: `.claude/references/security-checklist.md`.

## §1.1 — Secrets
- NEVER log, print, or write connection passwords to disk outside the secret store.
- Passwords go through `ISecretStore`. When no OS keyring is available the fallback stores secrets
  **unencrypted** (base64) under `~/.local/share/bearing/secrets/<guid>` — this is surfaced to the user
  (`SecretStorageSecure`, amber warning). DO NOT silently weaken or hide that posture.
- Platform keychain / real fallback encryption is deferred, not abandoned — don't remove the warning as
  a shortcut.

## §1.2 — Write guard (destructive SQL)
- `Bearing.Sql.WriteGuard` flags data-modifying / DDL statements (INSERT/UPDATE/DELETE/MERGE, DROP/
  TRUNCATE/ALTER, and data-modifying CTEs) on connections marked `RequireWriteConfirmation`.
- Keep the guard lexer-based and conservative. If you touch it, do not narrow what counts as risky without
  a test; the Production connection preset relies on it.

## §1.3 — Query log
- Executed SQL is logged to a local SQLite store (`SqliteQueryLog`) with retention pruning
  (`QueryLogRetentionDays`, default 180; ≤0 = keep forever). There is **no** literal/PII stripping — do not
  assume the log is sanitized. Don't add PII beyond the SQL text itself.

## §1.4 — Connections
- TLS is not enforced (sslmode is only set when the user adds the option). Don't hardcode credentials or
  disable certificate validation.
