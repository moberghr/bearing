# Lessons

Reusable corrections and gotchas captured during work.

## 2026-11-06 — inline results (AvaloniaEdit inline objects)

- **What happened:** A `Dispatcher.UIThread.Post` with no priority never ran in headless tests. **Rule:** pass
  `DispatcherPriority.Normal` (or higher) when the work must follow promptly. **Why:** `Default` is the lowest
  foreground priority, below `Loaded`, which is all `ShellHarness.Pump` drains — the app would run it late, the
  test never. **When:** any post whose effect a UI test asserts.
- **What happened:** Reading `Caret.VisualColumn` inside `Caret.PositionChanged` re-raised the event from inside
  itself (revalidation), and the nested call recorded the wrong "from" line. A passing run only appeared when
  debug output happened to read the column earlier. **Rule:** guard caret handlers against re-entrancy and
  capture "before" state before touching caret properties. **Why:** AvaloniaEdit caret getters have side
  effects. **When:** any `PositionChanged` handler that reads `VisualColumn`/`Position`.
- **What happened:** An inline-object row (TextEndOfLine + InlineObjectRun) is part of its line's visual line
  and has no caret stops, so Down snapped back to the line end forever. **Rule:** step over such rows using
  `Caret.DesiredXPos` (set only by Up/Down, assigned after the position — check it in a posted callback).
  **When:** any widget embedded between editor lines.
- **What happened:** The PostgreSQL split keeps an own-line comment followed by a blank line with the statement
  above, so `StatementSpan.TrimmedEnd` can sit on a comment line. **Rule:** for "where does this statement end
  on screen", ask `ISqlDialect.CodeEnd`. **When:** placing anything relative to a statement's end.
- **What happened:** "The buffer was replaced" was first inferred in the view (same tab, different text) and
  missed background-tab reloads. **Rule:** react where the replacement happens (`ScriptFileSync.Reload`,
  `LoadScriptIntoSelectedAsync`), not by diffing in `MainWindow`. **When:** per-tab state tied to buffer offsets.

## 2026-11-19 — DynamoDB provider (fourth engine)

- **What happened:** `dotnet build` failed NU1301 on a missing Visual Studio fallback package folder from the machine's
  global NuGet config; the repo's `nuget.config` clears sources but not fallback folders. **Rule:** on such a host pass
  `-p:RestoreConfigFile=<a config that clears <fallbackPackageFolders>>`; don't edit machine config. **When:** a red
  baseline that names a "local source" that doesn't exist.
- **What happened:** DynamoDB Local 3.1 rejected the placeholder access key `bearing-local` (hyphen) with "access key
  ID or security token is invalid"; unit tests only inspected the signed request and missed it. **Rule:** placeholder
  AWS keys are letters/digits only, and a sign-in path is proven by a live round trip, not a header assertion.
- **What happened:** an engine whose server identity lives in an `Options` entry (DynamoDB `Endpoint`) was invisible to
  `SameNetwork` / `SamePool`, so an endpoint-only edit kept the old schema and pool. **Rule:** any option that decides
  which server is reached belongs in both comparisons. **When:** adding a connection field outside Host/Port/Database/User.
- **What happened:** `ProviderTraits.For` falls back to Postgres for an unknown id, so registering a provider before its
  traits arm would silently pair it with the Postgres dialect. **Rule:** register a provider in the same commit as its
  traits arm.
- **What happened:** the App and the CLI each gathered scan facts; the copies drifted within a day (only one padded a
  SELECT the reduction skipped, so the App ran it unasked). **Rule:** a run/refuse decision both hosts need lives once
  in Sessions (§2.6), including its fail-closed padding.
- **What happened:** the spec assumed the PostgreSQL lexer reads PartiQL identically; seven divergent forms were found.
  **Rule:** decline divergent batches individually (widening only) rather than turning the guard off for the engine,
  which would empty the CLI allow-list.
