# Homebrew (macOS)

`bearing.rb` here is the **canonical source** of the cask — the tap repo holds a copy.

End users:

```bash
brew install --cask --no-quarantine moberghr/bearing/bearing
```

`--no-quarantine` is required: the app is unsigned (see *Signing* below). Without it macOS reports
"Bearing is damaged and can't be opened", which is Gatekeeper refusing a quarantined ad-hoc-signed
bundle rather than anything being wrong with the download.

## Why a cask and not a formula

Bearing is a GUI `.app`, so it goes in `/Applications` and Homebrew's cask DSL is what puts it there.
`auto_updates true` is the load-bearing stanza: Bearing updates *itself* through Velopack off its own
GitHub Releases feed (§9.6), in place. Without that stanza Homebrew sees a self-updated app as a version
mismatch and offers to reinstall the version it installed, undoing the update.

So `brew upgrade` is not part of the update path — it only matters when the cask itself is re-pointed.

## Apple Silicon only

`depends_on arch: :arm64`, and that is a property of the **release**, not of the app. A Velopack channel
holds one package per version, and the app reads the plain `osx` channel (`VelopackUpdateService` names no
channel, so Velopack's per-OS default applies). An `osx-x64` pack would therefore overwrite the arm64 one
in the same feed rather than sit beside it. Serving Intel means a second channel *and* an explicit channel
in the app — a change to the updater, not a flag in `build/velopack.sh`.

## Signing

There is no Moberg Developer ID, so the `.app` and the `.pkg` are unsigned and un-notarized. The .NET SDK
ad-hoc signs the apphost, which is enough to *execute* on Apple Silicon — an unsigned Mach-O binary will
not run there at all — but not enough to clear Gatekeeper on a quarantined download.

`build/velopack.sh` takes the whole of it from the environment, and passes nothing when it is absent
rather than claiming a signature it did not apply:

```bash
SIGN_APP_IDENTITY="Developer ID Application: …" \
SIGN_INSTALL_IDENTITY="Developer ID Installer: …" \
NOTARY_PROFILE=moberg \
  RID=osx-arm64 build/velopack.sh
```

WHEN a certificate exists, set those three in the `Publish osx-arm64` step of `.github/workflows/release.yml`,
drop `--no-quarantine` from the install line above, and delete the `caveats` block from the cask. Nothing
else changes.

## One-time: create the tap repo

The tap must be a GitHub repo named `homebrew-bearing` under the `moberghr` org (the `homebrew-` prefix is
required; `brew` strips it, which is what makes the user command read `moberghr/bearing/bearing`).

```bash
gh repo create moberghr/homebrew-bearing --public
git clone https://github.com/moberghr/homebrew-bearing
mkdir -p homebrew-bearing/Casks
cp packaging/homebrew/bearing.rb homebrew-bearing/Casks/bearing.rb
```

## Each release: publish, then re-point the cask

The macOS package is built by the `publish-macos` job in `.github/workflows/release.yml` — publishing a
GitHub Release is the whole trigger (§9.6a), and the job attaches `BearingSql-osx-Portable.zip`,
`BearingSql-osx-Setup.pkg`, the `.nupkg` and `releases.osx.json`. The cask installs the **zip**, because
that is the plain `.app` Velopack can then update in place; the `.pkg` is the by-hand route.

```bash
# 1. cut the release (tag + publish on GitHub); wait for publish-macos to finish.
# 2. compute the sha256 of the published zip
VERSION=0.7.1
URL="https://github.com/moberghr/bearing/releases/download/v${VERSION}/BearingSql-osx-Portable.zip"
SHA=$(curl -sL "$URL" | shasum -a 256 | cut -d' ' -f1)
echo "version $VERSION"
echo "sha256  $SHA"

# 3. edit version + sha256 in packaging/homebrew/bearing.rb, then copy it to the tap's
#    Casks/bearing.rb, commit, push.
# 4. verify:
brew install --cask --no-quarantine moberghr/bearing/bearing
brew audit --cask --strict --online moberghr/bearing/bearing
```

There is no version in the zip's filename, so the tag in the `url` is the only thing selecting a build —
a stale `version` with a fresh `sha256` fetches the *old* zip and fails the checksum rather than silently
installing the wrong one.

## Other channels

Windows (`Setup.exe`) and Linux (`.AppImage`) come off the same release from the `publish` job. See
`docs/RELEASING.md`.
