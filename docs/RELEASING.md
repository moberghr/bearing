# Releasing Bearing

Bearing ships through [Velopack](https://velopack.io): an installer plus per-file delta auto-update, with
**GitHub Releases on this repository** as the feed the app reads.

**Cutting a release is publishing a GitHub Release. That is the whole procedure.** No version bump, no
commit, no PR, no local build: `.github/workflows/release.yml` takes the version from the tag, builds both
platforms and uploads the assets onto the release you just published. `build/velopack.sh` is still the thing
that does the work — the workflow runs it once per platform — and still works by hand for a local package.

Two platforms are covered. CI gives each one the runner it targets; by hand, both can be built from one
machine, whichever OS it runs, because Velopack cross-builds Windows and Linux packages.
**macOS cannot be built off a Mac** (Velopack needs `codesign`,
`xcrun` and `productbuild`), so there is no macOS package; `build/release.sh` still explains the same for
its bare-binary path.

| RID | Output | Channel | Install |
|---|---|---|---|
| `win-x64` | `BearingSql-win-Setup.exe`, full/delta `.nupkg`, `BearingSql-win-Portable.zip` | `win` | per-user, `%LOCALAPPDATA%\BearingSql` |
| `linux-x64` | `BearingSql.AppImage`, full/delta `.nupkg` | `linux` | none — the AppImage runs where it sits |

## Cutting a release

1. **Releases ▸ Draft a new release** on GitHub.
2. Type the tag — `v0.5.5`: three parts, `v`-prefixed. It does not have to exist; GitHub creates it against
   the target branch when you publish. Velopack requires 3-part semver2 (`0.5.5`, `0.5.5-beta.1`), and both
   the workflow and the build script reject anything else before doing any work.
3. Write the description. That text *is* the release notes, everywhere — see [Release notes](#release-notes).
4. **Publish release.**

That is it. The workflow then, in order: runs the full suite against a real PostgreSQL, packs and publishes
`win-x64`, then packs and publishes `linux-x64`. Ten to fifteen minutes, and the Releases page grows a
`Setup.exe`, an `.AppImage`, the `.nupkg`s and both `releases.<channel>.json` feeds.

**Nothing in the tree names the version.** `<Version>` in `Directory.Build.props` is the placeholder
`0.0.0-dev`; the workflow passes the tag to `dotnet publish -p:Version=`, so `Help ▸ About` and the feed
agree by construction rather than by anyone remembering. A build from source reports `0.0.0-dev`, which is
what it is.

### If the build fails

The release goes **back to a draft** (the `draft-on-failure` job), because publishing is the trigger: a
visible release with no installers is a version `Help ▸ What's New` lists and nobody can install. Fix the
cause and hit **Publish release** again — re-publishing fires the workflow again.

If a later job fails after Windows already uploaded, the release is drafted with the Windows assets on it.
Re-publishing re-runs everything; `vpk` uploads what is missing and leaves what is already there.

### Re-running without touching the release

`Actions ▸ release ▸ Run workflow` takes a tag directly, for a release whose build failed for reasons
outside the code (a runner outage, a rate limit). It also offers **Skip the test run** — for unblocking a
release, not for routine use. The repository variable `RELEASE_SKIP_TESTS=true` does the same for the
release-triggered path; unset it once whatever it was covering for is fixed.

### Pre-releases

Tick **Set as a pre-release** and the workflow passes `vpk upload --pre`, then re-asserts the flag once both
platforms are up. That flag is load-bearing rather than a label: the updater and `Help ▸ What's New` both
filter pre-releases out, so a `v0.6.0-beta.1` is installable from the Releases page and is offered to
nobody. By hand, `PRERELEASE=1` does the same.

### Building one by hand

Still supported, and unchanged apart from where the version comes from:

```bash
dotnet tool install -g vpk       # the Velopack CLI (needs ~/.dotnet/tools on PATH)
gh auth login                    # publishing needs write access; the script reads `gh auth token`

VERSION=0.5.5 PUBLISH=1 RID=win-x64   build/velopack.sh
VERSION=0.5.5 PUBLISH=1 RID=linux-x64 build/velopack.sh
```

`VERSION` is what CI passes; omit it and the script falls back to `<Version>`, which is `0.0.0-dev`. The
script **refuses to build** unless `HEAD` carries the matching tag *and* that tag is on origin pointing at
`HEAD`, so a published version can never disagree with what About reports. `ALLOW_UNTAGGED=1` skips that
check for a throwaway local package (and blocks publishing).

Both platforms land on the same GitHub release (`--merge`); each channel carries its own
`releases.<channel>.json` and clients only read their own. The script fetches the previous release first so
this one ships as a **delta** as well as a full package — that is what keeps an update a few MB instead of
~65 MB. It is also why the two platform jobs run in sequence rather than in parallel, and why the workflow
takes a `concurrency` lock: two runs merging into one release is how a release ends up missing a channel's
feed.

`dist/velopack/<channel>` is wiped and repopulated from the feed on every run, deliberately: `vpk` reads
that directory as the release history, so a package left there by an earlier local build of the same
version makes it refuse to pack. The history has to come from what is actually published.

Useful switches: `SKIP_TESTS=1` (the script runs `dotnet test` by default), `CONFIG=Debug`.

Before publishing, the script also checks the tag is **on origin and points at HEAD**. That is not
belt-and-braces: `vpk upload github --tag` creates a missing tag at the default branch head, so a tag that
was never pushed would produce a release whose assets came from one commit and whose tag names another.

## Release notes

**What you type in the release description is the release notes.** The workflow reads it back off the
release and passes it to `vpk pack --releaseNotes`, so the same text is on the Releases page, inside the
package, and in the app.

Three sources, most specific first:

| Source | When |
|---|---|
| the GitHub release description | whenever it is non-empty — the normal case |
| `docs/release-notes/<version>.md` | a hand-build, or a release published with an empty description |
| commit subjects since the previous tag | neither of the above |

The description is never overwritten when it is where the notes came from (`KEEP_RELEASE_BODY=1`), and the
release's own title is passed back to `vpk upload --releaseName` so merging assets in cannot rename it. If
you publish with an empty description the generated notes are written *to* it — so the Windows job fills it
in and the Linux job reads that same text back, and both platforms ship identical notes either way.

`docs/release-notes/<version>.md` is no longer where you write notes for a normal release; it stays for the
history already there, and as the fallback above.

**The app now reads these notes back.** Help ▸ What's New renders the release history in Bearing itself,
the update strip links to the notes for the version it is offering, and the first launch after an update
opens them once. So a release description is no longer only a web page someone might visit — it is a dialog
every user is shown. Generated commit subjects are fine on the Releases page and thin inside the app; write
`docs/release-notes/<version>.md` for anything a user would want explained.

The app reads them from the GitHub Releases API, not from the package: `releases.<channel>.json` lists only
its own version, so it can describe the update on offer and nothing else. One source for the whole history
is why the in-app dialog and the Releases page can't disagree.

Generated notes are useful on their own, at least: this repo's commit subjects carry `(#nn)` refs, which
GitHub renders as links to the issues the release closed. But they are thin inside a dialog every user is
shown, so type a description.

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

## What CI covers, and what it does not

The `test` job is the only gate on a release, since there is still no CI on push ([#24]). It runs on Linux
against a real PostgreSQL — `build/test-db.sh`, the same script and the same pinned pagila the suites expect
locally — because the data-layer tests are `SkippableFact`s: without a server they go *quiet*, and a green
run full of skips is not evidence that the catalog reads, the TLS modes or the temporal mappings work (§4.2).

What #24 still wants and this does not give: nothing runs on push or on a pull request, and the platform
keychain tests (`PlatformKeychainTests`) still skip — they need macOS and Windows runners with a reachable
credential store, which is a build matrix, not a release job.

[#24]: https://github.com/moberghr/bearing/issues/24
