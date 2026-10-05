# Testing Rules (§4.x)

xUnit across `tests/Bearing.{App,Cli,Data,Persistence,Sql,Updates}.Tests` (+ `tests/Shared`).
Supplement: `.claude/references/dotnet/testing-supplement.md`.

## §4.1 — Framework & doubles
- **xUnit v2** (2.9.3) only. No NUnit/MSTest, and no package depending on `xunit.v3.*` (CS0433
  ambiguity — why `Avalonia.Headless.XUnit` isn't used). A v3 migration also means replacing
  `Xunit.SkippableFact`; it is deliberate, never a side effect.
- No mocking library — hand-rolled fakes in each project's `Fakes.cs`. No EF / InMemory.

## §4.2 — Live database tests skip, and a skip is not a pass
- Postgres: `SkippableFact` + `await PgTestServer.RequireAsync(factory)` (`tests/Shared/PgTestServer.cs`,
  defaults to `squirrel-pg-test` on 55434, override via `BEARING_TEST_PG_*`). SQL Server:
  `MsSqlTestServer` / `BEARING_TEST_MSSQL_*`, `bearing-mssql-test` on 1433.
- Never a plain `[Fact]` that fails without a DB; never a `catch { return false; }` probe.
- Containers: `./build/test-db.sh`, `./build/test-db-mssql.sh`. WHEN a change touches a provider, run its
  container before reporting a result.

## §4.3 — UI: headless tests assert, eyeball QA confirms
- NEVER report a UI change as visually verified. Say what was asserted; the user must eyeball-QA.
- Headless tests (`tests/Bearing.App.Tests/Ui/`) are for claims only a realized visual holds (a brush on a
  live cell, a scroll offset, a measured width, what input does to a real control). Logic that can be a pure
  helper goes in a helper first (§2.5).

## §4.4 — Assertions
Assert observable behaviour, not "does not throw". Match the target project's naming and fixtures.

## §4.5 — Headless UI harness (`Avalonia.Headless` 12.1)
- No `[AvaloniaFact]`: write `public Task Name() => _ui.Run(() => { … });` with `UiTestSession` injected,
  and `[Collection(UiTestCollection.Name)]` (no parallelism). Fresh `Application` per test, the real `App`
  via `AppBuilderFactory.Configure()`, Skia rendering (`UseHeadlessDrawing = false`).
- Use the real composition root (`ResultsHarness.Show`, `ShellHarness`). Never re-assemble a DataGrid.
- Grid virtualizes: give the window size, call `ResultsHarness.Pump`, keep fixtures small. Find cells via
  `ResultsHarness.Cell` (the `(row, column)` tag) — no test-only hooks in production visuals.
- Input only works on something genuinely focused (use `ShellHarness`, assert `IsFocused`). Assert the
  handler ran before asserting its effect.
- Setting `TextBox.Text` raises no `TextChanged` — type via `KeyTextInput` (`ConnectionEditorProbe.Type`).
  `ComboBox.SelectedIndex` does raise `SelectionChanged`.
- Synthetic drags need `RawInputModifiers.LeftMouseButton` on every move. Press a tab at its label (~20%
  across), not its centre.
- `async void` completion isn't reliably observable — assert the synchronous half, or test on the VM.
- Syntax colouring is not reliably assertable; verify by eye.
- Shared brushes must be immutable (`IImmutableBrush`); per-`Application` caches (`AtAlphaCached`). Don't
  disable collection parallelism to hide such bugs.

## §4.6 — Demo fixtures (`tests/Bearing.App.Tests/Demo/`)
- Prefer `DemoData` for UI data; let FK/PK/editability come from resolution, not set on the VM.
- **Never test FK/PK/editability resolution here** — that belongs in `Bearing.Data.Tests` against live
  pagila.
- Keep fixtures deterministic (no clocks, GUIDs). Fakes must follow the real executor loops.

## §4.7 — Live tests assert the server, not our assumptions
- When a live assertion fails, establish which side is wrong first:
  `docker exec squirrel-pg-test psql -U postgres -d pagila -Atc "…"`. Look up fixture contents; never
  assume (pagila sequences have no `OWNED BY`; `payment` is partitioned; sequences are `bigint`).
- Objects a test creates go in their own schema, dropped in `finally`. Cluster-wide teardown: one statement
  per call.
