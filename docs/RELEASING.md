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
gh auth login                    # the release feed is a private repo; the script reads `gh auth token`
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

## Updating from a private repo (interim)

The feed is a private repository, so reading it needs a GitHub token — and a token compiled into the app
would be a published secret, which is exactly the on-disk-secret posture §1.1 removed. So the app reads one
from the environment and never stores it:

```
BEARING_UPDATE_TOKEN=<a token with read access to moberghr/bearing>
```

Without it, the update check fails, reports one line in the status bar, and stops — no retries, no dialog.
**When the repository goes public, delete the variable and nothing else changes**: `UpdateFeed.AccessToken`
returns null and the same code path reads the public feed.

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
