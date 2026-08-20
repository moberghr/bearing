# Releasing Bearing

Bearing ships through [Velopack](https://velopack.io): an installer plus per-file delta auto-update, with
**GitHub Releases on this repository** as the feed the app reads. There is no CI yet ([#24]) — releases are
built from a working copy with `build/velopack.sh`.

Two platforms are covered. Both are built from one machine, whichever OS it runs, because Velopack can
cross-build Windows and Linux packages. **macOS cannot be built off a Mac** (Velopack needs `codesign`,
`xcrun` and `productbuild`), so there is no macOS package; `build/release.sh` still explains the same for
its bare-binary path.

| RID | Output | Channel | Install |
|---|---|---|---|
| `win-x64` | `BearingSql-win-Setup.exe`, full/delta `.nupkg`, `BearingSql-win-Portable.zip` | `win` | per-user, `%LOCALAPPDATA%\BearingSql` |
| `linux-x64` | `BearingSql.AppImage`, full/delta `.nupkg` | `linux` | none — the AppImage runs where it sits |

## One-time setup

```bash
dotnet tool install -g vpk       # the Velopack CLI (needs ~/.dotnet/tools on PATH)
gh auth login                    # publishing needs write access; the script reads `gh auth token`
```

## Cutting a release

1. Bump `<Version>` in `Directory.Build.props`. That single property is the app version everywhere: the
   assembly version, `Help ▸ About`, and the version the feed compares against. Velopack requires 3-part
   semver2 (`0.3.0`, `0.3.0-beta.1`) — a 4-part version is rejected.
2. Commit, then tag it: `git tag v0.3.0`. The script **refuses to build** unless `HEAD` carries the tag
   matching `<Version>`, so a published version can never disagree with what About reports. For a
   throwaway local package, `ALLOW_UNTAGGED=1` skips that check (and blocks publishing).
3. Build and publish each platform:

```bash
PUBLISH=1 RID=win-x64   build/velopack.sh
PUBLISH=1 RID=linux-x64 build/velopack.sh
```

Both land on the same GitHub release (`--merge`); each channel carries its own `releases.<channel>.json`
and clients only read their own. The script fetches the previous release first so this one ships as a
**delta** as well as a full package — that is what keeps an update a few MB instead of ~65 MB.

`dist/velopack/<channel>` is wiped and repopulated from the feed on every run, deliberately: `vpk` reads
that directory as the release history, so a package left there by an earlier local build of the same
version makes it refuse to pack. The history has to come from what is actually published.

Useful switches: `SKIP_TESTS=1` (the script runs `dotnet test` by default), `CONFIG=Debug`.

Before publishing, the script also checks the tag is **on origin and points at HEAD**. That is not
belt-and-braces: `vpk upload github --tag` creates a missing tag at the default branch head, so a tag that
was never pushed would produce a release whose assets came from one commit and whose tag names another.

## Release notes

The release description comes from `docs/release-notes/<version>.md` when that file exists. Write one for
anything worth explaining — it is the text people see on the Releases page, and it also travels inside the
package.

Without that file the notes are generated from the commit subjects since the previous tag. Because this
repo's subjects carry `(#nn)` refs, GitHub renders them as links to the issues the release closed, so the
generated notes are useful on their own. Either way the notes are passed to `vpk pack --releaseNotes` and
then written to the GitHub release body with `gh release edit`, so both platform runs produce the same text.

## The update token is optional

The repository is public, so the app reads the feed anonymously and needs no credential. `vpk download` and
the app's own check both work with nothing set.

`BEARING_UPDATE_TOKEN` remains as an opt-in for the cases where anonymous isn't enough — GitHub's
unauthenticated API limit is 60 requests/hour per IP, which a shared egress address can exhaust, and a
private fork of this repo would need one. Wherever it is used it comes from the environment and is never
written to disk: a token compiled into the binary is a published secret, and an on-disk one is the posture
§1.1 removed. A token that fails is reported once in the status bar and not retried.

## What the installer does and does not own

Velopack owns `%LOCALAPPDATA%\BearingSql` (its `current\`, `packages\`, `Update.exe` and the stub exe) and
**deletes that directory on uninstall**. The pack id is therefore deliberately *not* `bearing`: user state
lives in `%APPDATA%\bearing` (settings, keybindings, recent projects) and `%LOCALAPPDATA%\bearing` (query
log, default project), so an uninstall would otherwise take the user's query history with it. See §9.6.
Passwords are in the OS credential store and are untouched by an install, update or uninstall.

`packId` is also the permanent update identity — renaming it orphans every installed client, which is a
manual re-install for each of them.

## Applying an update

The app checks once per launch (setting: *General ▸ Download updates automatically*), downloads in the
background, and then waits. Restarting is the user's click: it stages the update for install-on-exit and
closes the window the ordinary way, so the unsaved-work prompt, editor flush, session save and connection
disposal all still run. Nothing is ever installed under a running query.

`Help ▸ Check for Updates…` does the same on demand and reports the outcome either way. Both are no-ops in
a build that was not installed by the installer (running from source, or from `build/release.sh`'s
archive) — there is no installed layout to replace.

## The archive path is still there

`build/release.sh` is unchanged and still produces the single-file `.tar.gz` / `.zip` with their own
install scripts. It has no update path; keep it for anyone who wants a portable build, and for the record
of how the app was shipped before this.

[#24]: https://github.com/moberghr/bearing/issues/24
