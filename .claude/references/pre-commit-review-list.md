# Pre-Commit Review List — Squirrel

Fast, high-signal checks for staged changes. Max 10. Tailored to this repo (Avalonia desktop, raw ADO, no EF/MediatR).

1. **No secrets committed** — no passwords, tokens, or connection strings in source, config, or test fixtures.
2. **No password in logs/status** — connection passwords never reach logs, `StatusText`, or disk outside `ISecretStore` (§1.1).
3. **`Squirrel.Core` stays dependency-free** — no new `PackageReference`/`ProjectReference` added to Core (§2.1).
4. **No logic in views** — DB/connection/SQL logic is not added to `Views/*.axaml.cs` or `Controls/` (§2.2).
5. **God objects not grown** — changes to `ResultView.cs` / `MainWindow.axaml.cs` / `MainWindowViewModel.cs` extract rather than append (§9.1).
6. **`CancellationToken` propagated** — every new async DB call flows the token through (§5.1).
7. **Postgres tests are skip-safe** — new DB tests use `SkippableFact` + `SQUIRREL_TEST_PG_*`, not plain `[Fact]` (§4.2).
8. **DML stays parameterized** — write paths use parameters; only the SQL-preview inlining is display-only (§5.4).
9. **Shortcuts via the input pipeline** — new keybindings register a command + keymap entry, not a hand-rolled `OnKeyDown` branch (§9.2).
10. **Tests for new public logic** — new pure helpers have unit tests; UI changes are noted as needing user eyeball QA (§4.3).
