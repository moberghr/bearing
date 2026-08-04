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
- To run them live: `BEARING_TEST_PG_PORT=5434 dotnet test`.

## §4.3 — UI is not headlessly testable
- Avalonia UI cannot be driven headlessly on Wayland (no synthetic input, no self-screenshot). Cover UI
  behavior by extracting pure logic (see §9.x / §2.5) and unit-testing that.
- NEVER report a visual/interaction change as "verified" — state that it builds and tests pass, and that
  the user must eyeball-QA the running app.

## §4.4 — Assertions
- Assert observable behavior, not just "does not throw". Match existing naming and fixture patterns in the
  target test project.
