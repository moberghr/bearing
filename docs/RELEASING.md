# Releasing Bearing

Bearing ships through [Velopack](https://velopack.io): an installer plus per-file delta auto-update, with
**GitHub Releases on this repository** as the feed the app reads.

**Cutting a release is one step: publish a GitHub release.** `.github/workflows/release.yml` runs the tests,
builds both platforms and uploads them. Everything below the next section is what that workflow does, kept
because it still runs by hand when you want it to.

Two platforms are covered. Both are built from one machine, whichever OS it runs, because Velopack can
cross-build Windows and Linux packages. **macOS cannot be built off a Mac** (Velopack needs `codesign`,
`xcrun` and `productbuild`), so there is no macOS package; `build/release.sh` still explains the same for
its bare-binary path.

| RID | Output | Channel | Install |
|---|---|---|---|
| `win-x64` | `BearingSql-win-Setup.exe`, full/delta `.nupkg`, `BearingSql-win-Portable.zip` | `win` | per-user, `%LOCALAPPDATA%\BearingSql` |
| `linux-x64` | `BearingSql.AppImage`, full/delta `.nupkg` | `linux` | none — the AppImage runs where it sits |

## One-time setup

Only for building by hand — the workflow installs its own `vpk` and uses the run's `GITHUB_TOKEN`.

```bash
dotnet tool install -g vpk       # the Velopack CLI (needs ~/.dotnet/tools on PATH)
gh auth login                    # publishing needs write access; the script reads `gh auth token`
```

## Cutting a release

Create the release in GitHub — **Releases ▸ Draft a new release**, pick or create the `v*` tag, write the
description, publish. That is the whole job. The workflow then:

1. runs `dotnet test` over the solution, and stops if anything fails — before any asset is public;
2. builds `win-x64` and `linux-x64` and uploads both to that release;
3. checks the release really carries `releases.<channel>.json` and the full package, and fails if not.

The version is the tag. Nothing to bump, and nothing that can disagree with it.

A description you type in the UI is kept. `docs/release-notes/<version>.md` still wins where it exists,
because that copy is what the app reads back through **Help ▸ What's New** and what travels inside the
package — write one for anything worth explaining.

**The release is live before its assets are.** That is inherent to reacting to a release you published: for
the few minutes the tests and build take, the page exists, watchers have been notified, and there is nothing
to download. It looks exactly like a broken release because, briefly, it is one.

So a failure anywhere in the job **returns the release to draft** and says so. The tag and the description
survive; fix the cause and re-run the workflow. Re-running is safe — `--merge` and the asset check are both
idempotent — and a release that never reaches the end is never left published and empty, which is the state
`v0.5.4` has been in since it was cut.

There is no macOS package, here or anywhere (see above).

`workflow_dispatch` runs the test job alone against any ref, which answers "would this tag build" without
creating a release that claims it did.

### Pre-releases

Tick **Set as a pre-release** and the workflow carries that through to `vpk upload --pre`, then re-asserts
the flag once the assets are up. A tag with a pre-release identifier — `v0.6.1-beta.1` — counts on its own,
checkbox or not.

The flag is load-bearing rather than a label: the updater and `Help ▸ What's New` both filter pre-releases
out, so a beta is installable from the Releases page and offered to nobody. Losing it is the whole reason it
is asserted twice. By hand, `PRERELEASE=1` does the same.

### By hand

The same script the workflow runs, for when you want the packages locally or CI is not an option.

1. **Tag the commit you mean to release**, and push it:

   ```bash
   git tag v0.6.0 && git push origin v0.6.0
   ```

   The tag is the version. There is no `<Version>` property to bump — [MinVer](https://github.com/adamralph/minver)
   reads the nearest `v*` tag and feeds it to the assembly version, `Help ▸ About`, and the string the
   update feed compares against. Velopack requires 3-part semver2 (`0.6.0`, `0.6.0-beta.1`); the script
   rejects anything else before it starts building.

   This used to be two steps — bump the property, then tag — and they drifted: `v0.5.4` landed on the
   commit *before* the bump, so the tag said 0.5.4 while the build said 0.5.3. One value read once cannot
   do that.

   A commit with no tag has no release version, and the script refuses to build one. For a throwaway local
   package `ALLOW_UNTAGGED=1` builds `<last-tag>-local.<sha>` instead (and blocks publishing).
2. Write `docs/release-notes/<version>.md` — see below. Do this before publishing: the script reads it for
   the release body, and the app shows it to every user.
3. Build and publish each platform:

```bash
PUBLISH=1 RID=win-x64   build/velopack.sh
PUBLISH=1 RID=linux-x64 build/velopack.sh
```

Both land on the same GitHub release (`--merge`); each channel carries its own `releases.<channel>.json`
and clients only read their own. After uploading, the script **checks the release actually carries**
`releases.<channel>.json` and the full package, and fails if it does not — an empty release page is not a
release, and `v0.5.4` shipped as exactly that: tagged, described, and invisible to every installed copy,
with nothing to say so. The script fetches the previous release first so this one ships as a
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

**The app now reads these notes back.** Help ▸ What's New renders the release history in Bearing itself,
the update strip links to the notes for the version it is offering, and the first launch after an update
opens them once. So a release description is no longer only a web page someone might visit — it is a dialog
every user is shown. Generated commit subjects are fine on the Releases page and thin inside the app; write
`docs/release-notes/<version>.md` for anything a user would want explained.

The app reads them from the GitHub Releases API, not from the package: `releases.<channel>.json` lists only
its own version, so it can describe the update on offer and nothing else. One source for the whole history
is why the in-app dialog and the Releases page can't disagree.

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
