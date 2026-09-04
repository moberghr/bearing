# Testing Rules (§4.x)

xUnit across four test projects (`Bearing.{Sql,App,Data,Persistence}.Tests`).
Supplement: `.claude/references/dotnet/testing-supplement.md`.

## §4.1 — Framework & doubles
- xUnit only, and **xUnit v2** (2.9.3). Do not introduce NUnit or MSTest, and do not pull in a package that
  depends on `xunit.v3.*` — both cores in one project makes every `[Fact]`/`[Theory]`/`[InlineData]`
  ambiguous (CS0433), which is why `Avalonia.Headless.XUnit` is not used (§4.5). Moving the suite to v3 also
  means replacing `Xunit.SkippableFact`, which has no v3 build and which `PgTestServer.RequireAsync` is
  built on: that is a deliberate migration, not a side effect of adding a package.
- No mocking library (no Moq/NSubstitute). Use the hand-rolled fakes in each project's `Fakes.cs`; add
  new fakes there rather than pulling in a framework.
- No EF Core / `UseInMemoryDatabase` — this project uses raw ADO, so tests exercise real query behavior.

## §4.2 — Skip-safe Postgres integration tests
- Tests needing a live Postgres use `Xunit.SkippableFact` and read `BEARING_TEST_PG_*` env vars,
  defaulting to a local docker container. They **skip** (never fail) when no server is reachable. The
  SQL Server suites work the same way off `BEARING_TEST_MSSQL_*` (`tests/Shared/MsSqlTestServer.cs`).
- **A skip is not a pass, and the difference is measured.** Both engines have a script that creates the
  container its defaults point at — `./build/test-db.sh` and `./build/test-db-mssql.sh` — and the second
  did not exist for the whole of the SQL Server work. Those suites therefore skipped, the build stayed
  green, and every claim about the `sys.*` catalog queries, column origin and batch row counts was argued
  rather than observed. Their first live run failed 8 of 177, **6 of them real provider bugs**: asking for
  column origin made `CREATE VIEW` fail (Msg 111), un-aliased columns carried no origin at all so FK
  navigation and inline editing were dead on `select *`, and an error number missing from the
  uncountable-shape set threw a server error at the user instead of hiding a total. WHEN a change touches
  a provider, run its container before reporting a result.
- WHEN adding a Postgres-dependent test, DO NOT write a plain `[Fact]` that fails without a DB — follow the
  `SkippableFact` + `BEARING_TEST_PG_*` pattern in `tests/Bearing.Data.Tests`.
- To run them live: just `dotnet test`. Host/port/db/user/password come from `PgTestServer`
  (`tests/Shared/PgTestServer.cs`, linked into both test projects) and already default to the
  `squirrel-pg-test` container on 55434; override any of them with `BEARING_TEST_PG_*`.
- WHEN a test needs the server, call `await PgTestServer.RequireAsync(factory)` rather than writing a
  `catch { return false; }` reachability probe. The helper reports *why* it skipped — a bare bool collapses
  "wrong port", "wrong password", "no such database" and "nothing listening" into one useless message, which
  is how a stale 5433 default sat unnoticed while another project's Postgres answered on that port.

## §4.3 — UI: headless tests answer *did this change*, eyeball QA answers *does it look right*
Superseded 2026-08-31 (#62). This rule used to read "Avalonia UI cannot be driven headlessly on Wayland",
and that conflated two different capabilities. What Wayland blocked was driving the *real running app* —
synthetic input into a live window, self-screenshotting — and that is still true (and moot on Windows).
`Avalonia.Headless` never touches a display server on any OS: it substitutes the windowing platform
in-process. Realized controls, layout passes, synthetic input and rendered pixels are all available.

**Still true, and the part to keep:** NEVER report a UI change as visually verified. A headless test asserts
a specific property, offset or measurement — never that the result looks right. Say what was asserted, and
that the user must eyeball-QA the running app.

**What a headless UI test is for** (`tests/Bearing.App.Tests/Ui/`, see §4.5): a claim about a realized
visual that no pure helper can hold — a brush or font style on a live cell, a `ScrollViewer` offset across a
layout pass, a measured column width, what a keystroke or click does to a control that exists. #61 (a NULL
in an FK column rendering upright and bright) is the shape of it: three cell kinds each built their own
`TextBlock`, so the drift was in the visual, not in any function.

**What it is not for:** logic that could be a pure helper. Extraction (§2.5) is still the first move — it is
faster, parallelizable, and reads better. Reach for a UI test when the visual tree *is* the subject.

## §4.4 — Assertions
- Assert observable behavior, not just "does not throw". Match existing naming and fixture patterns in the
  target test project.

## §4.5 — How the headless UI harness works
`tests/Bearing.App.Tests/Ui/` — `Avalonia.Headless` 12.1.0, driven directly.

- **No `[AvaloniaFact]`.** `Avalonia.Headless.XUnit` is xUnit v3 only (§4.1). `[AvaloniaFact]` is a thin
  wrapper over `HeadlessUnitTestSession`, which is plain public API, so `UiTestSession.Run` wraps that
  instead. A UI test is written `public Task Name() => _ui.Run(() => { … });` and takes `UiTestSession` as a
  constructor parameter.
- **One collection, no parallelism.** Every UI test class carries `[Collection(UiTestCollection.Name)]`.
  Avalonia state is thread-affine and two applications in one process is not a supported shape, so the
  collection is declared `DisableParallelization = true`. UI tests therefore serialize — keep the suite
  focused rather than exhaustive.
- **A fresh `Application` per test** (`AvaloniaTestIsolationLevel.PerTest`). Required, not tidiness:
  `App.SetConnectionAccent` mutates the shared `ConnectionBrush` in place (§9.3).
- **The real `App`**, through `AppBuilderFactory.Configure()` — the same call the desktop entry point makes,
  so tests resolve the same token dictionaries and control themes the app ships. Nothing in
  `App.OnFrameworkInitializationCompleted` runs (its body is guarded on
  `IClassicDesktopStyleApplicationLifetime`), so no query log, settings file or update check is touched.
- **Skia, not headless drawing** (`UseHeadlessDrawing = false`). The stub does no text shaping, and text
  measurement is load-bearing in the results grid — initial column widths derive from it (#30), as do
  ellipsization and scroll offsets. A stub that measured every string the same would agree with itself and
  not with the app.
- **Use the real composition root.** `ResultsHarness.Show` assigns `ResultView.Results` and lets the view
  build itself. DO NOT re-assemble a DataGrid in a test: a harness that builds its own grid lets
  `ResultView.BuildGrid` drift out from under the suite, which is the one failure mode a UI test suite must
  not have.
- **The DataGrid virtualizes.** A cell has no visual until the window has an explicit size and layout has
  run; rows below the fold have none at all. Give the window room (`ResultsHarness.Show` uses 1000×700),
  call `ResultsHarness.Pump`, and keep fixtures small or scroll first. `Pump` runs layout and drains the
  dispatcher twice over, because the grid's own corrections (scroll-into-view, current-cell adoption) are
  posted at `DispatcherPriority.Loaded` and land a frame later.
- **Find cells by the tag the app already sets.** `ResultCellFactory.MakeSelectable` stamps `(row, column)`
  on each cell's selection border for drag hit-testing; `ResultsHarness.Cell` reads that. Do not add
  test-only names or hooks to production visuals.
- **Synthetic keys and text input work — through the shell.** `window.KeyTextInput(...)`,
  `KeyPress`/`KeyRelease` and `MouseDown`/`MouseUp` all reach the real handlers once something is genuinely
  focused, which `ShellHarness` gives you (`editor.TextArea.Focus()` then assert `IsFocused`). That is how
  #70's auto-close is tested end to end: typing a quote, stepping over a closer, Enter escaping the pair.
  What does *not* work is a bare control in a plain `Window` — focus never lands, the handler never fires,
  and a test written that way passes while testing nothing. So always assert the handler ran (or that the
  control is focused) before asserting what it did.
- **Assigning `TextBox.Text` from a test raises no `TextChanged`.** Measured on Avalonia 12.1, with and
  without a shown window and a layout pass: the event comes off the edit path, not off the property. So a
  test that sets `.Text` and asserts what a `TextChanged` handler did asserts nothing, and passes — the box
  holds the new text while the model under it never moved. Drive it the way a user does instead: focus the
  box, assert `IsFocused`, `SelectAll()` if you mean to replace, then `window.KeyTextInput("…")` (which
  takes a whole string) and pump. `ConnectionEditorProbe.Type` is the helper.
- **A `ComboBox.SelectedIndex` assignment *does* raise `SelectionChanged`**, with no window and no layout
  pass — which is why the connection editor's engine-picker and credential-dropdown tests need neither.
  Code-built rows are findable straight off `GetLogicalDescendants()` by the `{Key}Box` name the dialog
  stamps on them, because they are built in the constructor. Reach for `Show()` only when text input has to
  land somewhere.
- **Syntax colouring resists a deterministic assertion.** The TextMate grammar colours a line as its visual
  line is drawn, so reading `ShapedTextRun.Properties.ForegroundBrush` back can give the plain theme
  foreground for a line the tokenizer has not reached — and "the comment's colour does not appear below it"
  then passes because nothing is coloured at all. Pumping to a stable signature did not close it (still ~1
  run in 3 red over eight runs), so a colouring suite was written and then dropped rather than shipped
  flaky. Verify highlighting by eye; #69 was investigated this way and did not reproduce — LF and CRLF,
  wholesale load and incremental edit, and every comment shape that could plausibly leak tokenizer state
  (no space after the dashes, trailing on a code line, inside a statement, last line, and a line comment
  containing `/*`, an apostrophe or a quoted word). The **one** shape that colours the rest of the buffer
  green is an unterminated `/*`, which is correct — Postgres comments to end of file too — and is invisible
  to execution, because Bearing runs the statement under the caret rather than the file.
- **The whole shell is testable** — `ShellHarness` builds `MainWindow` over a real `ShellViewModel` and
  shows it, so keyboard focus, the tab strip's two-way selection binding and the key routing are all
  assertable (that is how #87 and #88 are covered). It is the heaviest harness here: a query log and a
  project directory per instance, so reach for `ResultsHarness` or a plain unit test where the window is not
  the point.
- **Two real bugs had to be fixed before the shell harness could work, and both are worth not
  re-introducing.** Shared brushes must be **immutable**: a mutable `SolidColorBrush` is an `AvaloniaObject`
  and takes the dispatcher of whichever thread constructed it, so a static cache filled on an xunit worker
  thread threw `VerifyAccess` out of the compositor the moment a visual on the dispatcher thread used it
  (`ThemeBrush.AtAlpha` returns `IImmutableBrush` for exactly this reason). And a token cache must be keyed
  per `Application` (`ThemeBrush.AtAlphaCached`): the value depends on whether one exists, so an
  unconditional static let whichever test ran first decide it for every later one — which made
  `EnvironmentWashTests`' no-Application fallback assertions pass or fail on test order. The alternative,
  `parallelizeTestCollections: false`, was tried and rejected: it hid both bugs instead of fixing them.
- **An `async void` handler's completion is not reliably observable from a UI test.** Closing a tab through
  synthetic input does close it, and asserting that it *has* closed passed alone and failed inside its own
  class — the outcome depended on what an earlier test in the collection had left on the shared dispatcher,
  and neither `Pump()` nor an unfiltered `RunJobs()` loop nor an `await Task.Yield()` made it deterministic
  (the last made it worse). Assert the **synchronous** half instead: for a press, that the event was marked
  handled, which is what a routing fix actually changes. A test that needs the completion belongs on the
  view model, where the close can be awaited directly.
- **Available and unused so far:** synthetic input on any `TopLevel` (`MouseDown`/`MouseMove`/`MouseUp`/
  `MouseWheel`, `KeyPress`/`KeyPressQwerty`, `KeyTextInput`, `SetRenderScaling`, and `DragDrop`, which takes
  an `IDataTransfer` and so already matches the v12 typed API, §9.3), plus real pixels via
  `AvaloniaHeadlessPlatform.ForceRenderTimerTick(n)` + `CaptureRenderedFrame()`.

## §4.6 — The demo fixtures are not where resolution is tested
`tests/Bearing.App.Tests/Demo/` (`DemoData` + `DemoProvider`, #63) serves hand-authored result sets and a
hand-built `SchemaSnapshot` so the UI can be driven with no Postgres at all. It works at full fidelity
because `ColumnDescriptor.BaseTableId`/`BaseColumnOrdinal` are provider-assigned and provider-neutral (§5.1):
a fixture declares a column's origin and gets real FK navigation, real inline editing and real PK badges out
of `ResultSetBuilder` and the resolvers.

- WHEN a UI test needs data, prefer `DemoData` over hand-rolling a `QueryResult` — and let the affordances
  come from resolution. Setting `ForeignKeyColumns`/`PrimaryKeyColumns`/`EditTarget` on the view model
  directly tests the view against a claim no code made.
- **DO NOT test FK / PK / editability *resolution* here.** These fixtures encode our assumptions about what
  Postgres reports, so asserting them back yields a green suite over a broken app. Resolution belongs in
  `Bearing.Data.Tests` against live pagila (§4.2). What belongs here is how the UI behaves **given** a result
  shape.
- Deterministic on purpose — fixed ids, values, row order and durations, no clocks and no GUIDs, because
  captures get diffed and assertions count rows. Keep it that way when adding a fixture.
- `DemoExecutor` implements the awkward paths too, not just `ExecuteAsync`: batched streaming with the
  `MaxRows` ceiling and the `Truncated` flag, a count that can be a number / a blank / a throw, and
  `ExecuteWriteAsync` recording the generated DML so §5.4's parameterization is assertable without a server.
  A page query comes back **without** column origins, as it does from the real provider.
