# Seams already built

Extension points this codebase already has. **Use them instead of adding a parallel path** — each one is
cheaper than what you'd write from scratch, and most already have the tests that keep them honest.

Work items live in GitHub issues: <https://github.com/moberghr/bearing/issues>. This file is the standing
"how to land it" companion to those — the rules in `.claude/rules/` say what is required, this says what is
already available.

- **Settings** — anything that wants to be configurable costs one `AppSettings` property plus one
  `SettingsCatalog` descriptor; it then renders, searches, persists and resets for free.
  `SettingsCatalogTests` fails the build if a property has neither a descriptor nor an explicit
  hidden-state entry, so the catalog can't drift behind the model.
- **Tabular text/file formats** — a new format (Parquet, a `VALUES` list, `insert … on conflict`) is a
  function over `Results/TableBlock` in `TableFormats` plus one enum member added to
  `CopyRenderer.Alternatives`; it then works for **both** Copy as ▸ and Export, and is unit-testable
  without a grid.
- **Grid actions** — `Controls/ResultContextMenu` is the discoverable home for anything new in the grid
  (Paste belongs there next to Copy). The tab strip has no equivalent yet; issue #3 builds it.
- **Confirming a write** — build a `Services/WriteConfirmation` (connection, action, verbs, statements) and
  the dialog derives all its display text from it; anything that wants to *show* SQL reuses
  `Controls/SqlStatementList`. Note the guarded path deliberately does **not** commit on Enter.
- **Quit-time intervention** — hangs off `MainWindow.OnClosing`, whose block path deliberately does not
  call `base` (that is what raises `Closing`, which saves the session and disposes live connections —
  running it for a close that isn't happening would kill the queries the user just chose to keep).
- **Keyboard** — register a command in `CommandIds` + `KeymapDefaults` and let `KeyDispatcher` route it
  (§9.2); never add an `OnKeyDown` branch. Grid *spatial* navigation is the one deliberate exception.
  Register a scope's commands **before** `keybindings.json` loads — the loader rejects bindings for ids it
  hasn't seen.
- **Pure logic** — `Results/`, `Input/`, or `Bearing.Sql` (§2.5). On this project that isn't style advice:
  extracting the pure part is the *only* way to get behaviour under test, because the UI can't be driven.
- **`ConnectionInfo.Options`** — app-level keys (`entra.*`) work now: the factory filters reserved
  credential/identity keys, ignores keys Npgsql doesn't own, and still applies genuine keywords.

## Two standing cautions

- **GUI can't be driven headlessly here (Wayland), so every UI change needs a manual pass** (§4.3). Never
  report a visual or interaction change as verified — say it builds, tests pass, and the user must eyeball it.
  Two surfaces can't be seen on the dev box at all: the connection dialog's no-keyring warning and
  Settings ▸ Security only appear on a machine *without* libsecret.
- **Keep file:line references honest** — the migrated issues carry a lot of them, and they were accurate when
  written, not necessarily now. The drift to watch for is code moving between partials (`MainWindow`,
  `ResultView`, `ShellViewModel` are all composition roots over several files), which silently invalidates
  pre-split line numbers. Re-check before trusting one.
