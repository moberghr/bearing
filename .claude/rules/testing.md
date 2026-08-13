# Testing Rules (§4.x)

xUnit across four test projects (`Bearing.{Sql,App,Data,Persistence}.Tests`).
Supplement: `.claude/references/dotnet/testing-supplement.md`.

## §4.1 — Framework & doubles
- xUnit only. Do not introduce NUnit or MSTest.
- No mocking library (no Moq/NSubstitute). Use the hand-rolled fakes in each project's `Fakes.cs`; add
  new fakes there rather than pulling in a framework.
- No EF Core / `UseInMemoryDatabase` — this project uses raw ADO, so tests exercise real query behavior.

## §4.2 — Skip-safe Postgres integration tests
- Tests needing a live Postgres use `Xunit.SkippableFact` and read `BEARING_TEST_PG_*` env vars,
  defaulting to a local docker container. They **skip** (never fail) when no server is reachable.
- WHEN adding a Postgres-dependent test, DO NOT write a plain `[Fact]` that fails without a DB — follow the
  `SkippableFact` + `BEARING_TEST_PG_*` pattern in `tests/Bearing.Data.Tests`.
- To run them live: just `dotnet test`. Host/port/db/user/password come from `PgTestServer`
  (`tests/Shared/PgTestServer.cs`, linked into both test projects) and already default to the
  `squirrel-pg-test` container on 5434; override any of them with `BEARING_TEST_PG_*`.
- WHEN a test needs the server, call `await PgTestServer.RequireAsync(factory)` rather than writing a
  `catch { return false; }` reachability probe. The helper reports *why* it skipped — a bare bool collapses
  "wrong port", "wrong password", "no such database" and "nothing listening" into one useless message, which
  is how a stale 5433 default sat unnoticed while another project's Postgres answered on that port.

## §4.3 — UI is not headlessly testable
- Avalonia UI cannot be driven headlessly on Wayland (no synthetic input, no self-screenshot). Cover UI
  behavior by extracting pure logic (see §9.x / §2.5) and unit-testing that.
- NEVER report a visual/interaction change as "verified" — state that it builds and tests pass, and that
  the user must eyeball-QA the running app.

## §4.4 — Assertions
- Assert observable behavior, not just "does not throw". Match existing naming and fixture patterns in the
  target test project.
