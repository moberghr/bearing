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
