#!/usr/bin/env bash
#
# Bearing release builder — Velopack edition (#20).
#
# Produces an installer plus a self-updating package set, and (optionally) publishes it to GitHub
# Releases, which is the feed the app itself reads. Unlike build/release.sh this is NOT a single-file
# publish: Velopack updates are per-file deltas, so a single compressed exe would make every update a
# full re-download.
#
#   win-*    → Setup.exe + full/delta .nupkg + releases.win.json     (channel "win")
#   linux-*  → self-updating .AppImage + .nupkg + releases.linux.json (channel "linux")
#
# macOS is not buildable from here at all: Velopack needs codesign/xcrun/productbuild, so a .app/.pkg
# requires a Mac. build/release.sh says the same about its own bare-binary path.
#
# Usage:
#   build/velopack.sh                          # win-x64, build only
#   RID=linux-x64 build/velopack.sh            # cross-build the AppImage from Windows
#   PUBLISH=1 build/velopack.sh                # ...and upload to GitHub Releases (needs gh auth)
#   SKIP_TESTS=1 build/velopack.sh             # skip the test run
#   ALLOW_UNTAGGED=1 build/velopack.sh         # build a version HEAD isn't tagged for (local testing)
#
# Requires: dotnet, vpk (dotnet tool install -g vpk), and gh for the GitHub steps.
#
set -euo pipefail

# --- Locate repo root (this script lives in <root>/build) ---------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

# --- Config -------------------------------------------------------------------
RID="${RID:-win-x64}"
CONFIG="${CONFIG:-Release}"
PROJECT="src/Bearing.Desktop/Bearing.Desktop.csproj"
REPO_URL="https://github.com/moberghr/bearing"

# The Velopack app identity. NOT "bearing": the Windows installer owns %LocalAppData%\<packId> and
# deletes it on uninstall, and %LOCALAPPDATA%\bearing is where BearingPaths keeps the query log and the
# default project — an uninstall would take the user's history with it. See .claude/rules §9.6.
# This id is also the permanent update identity: changing it orphans every installed client.
PACK_ID="BearingSql"
PACK_TITLE="Bearing"
PACK_AUTHORS="Moberg"

case "$RID" in
  win-*)
    OS_FAMILY=windows; DIRECTIVE="[win]"; CHANNEL="win"
    MAIN_EXE="bearing.exe"
    ICON="assets/brand/icons/bearing.ico"
    ;;
  linux-*)
    OS_FAMILY=linux; DIRECTIVE="[linux]"; CHANNEL="linux"
    MAIN_EXE="bearing"
    ICON="assets/brand/icons/png/tile-512.png"
    ;;
  osx-*)
    echo "ERROR: macOS packages cannot be built off a Mac." >&2
    echo "       Velopack depends on codesign / xcrun / productbuild; run this script on macOS." >&2
    exit 2
    ;;
  *) echo "ERROR: unrecognised RID '$RID' (expected win-* or linux-*)." >&2; exit 2 ;;
esac

# --- Tooling ------------------------------------------------------------------
if ! command -v vpk >/dev/null 2>&1; then
  echo "ERROR: 'vpk' is not on PATH. Install it with:" >&2
  echo "         dotnet tool install -g vpk" >&2
  echo "       then make sure ~/.dotnet/tools is on PATH (a new shell usually has it)." >&2
  exit 2
fi

# --- Version: Directory.Build.props is the single source of truth -------------
VERSION="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' Directory.Build.props | head -1)"
if [[ -z "$VERSION" ]]; then
  echo "ERROR: couldn't read <Version> from Directory.Build.props." >&2
  exit 1
fi

# The feed must never disagree with Help ▸ About, so the build refuses a version HEAD isn't tagged for.
TAG="v$VERSION"
HEAD_TAG="$(git describe --exact-match --tags HEAD 2>/dev/null || true)"
if [[ "$HEAD_TAG" != "$TAG" ]]; then
  if [[ "${ALLOW_UNTAGGED:-0}" == "1" ]]; then
    echo "WARNING: HEAD is not tagged $TAG (ALLOW_UNTAGGED=1) — do not publish this build."
  else
    echo "ERROR: HEAD is not tagged $TAG (found: ${HEAD_TAG:-none})." >&2
    echo "       Bump <Version> in Directory.Build.props, commit, then:  git tag $TAG" >&2
    echo "       Or set ALLOW_UNTAGGED=1 to build a throwaway package locally." >&2
    exit 1
  fi
fi

TAG_STATUS=""

if [[ "${PUBLISH:-0}" == "1" && "${ALLOW_UNTAGGED:-0}" == "1" ]]; then
  echo "ERROR: refusing to PUBLISH an untagged build." >&2
  exit 1
fi

# The tag must also be on the remote, and point where we think it does. `vpk upload github --tag` creates a
# missing tag at the DEFAULT BRANCH HEAD, so a tag that was never pushed yields a release whose assets were
# built from one commit and whose tag names another — silently. Checked before the long build, not after.
if [[ "${PUBLISH:-0}" == "1" ]]; then
  HEAD_SHA="$(git rev-parse HEAD)"
  # An annotated tag lists twice: the tag object, then the peeled commit as "<tag>^{}". The peeled line is
  # the one to compare — the tag object's own sha is not a commit and would never match HEAD. A lightweight
  # tag has only the first line, which already is the commit.
  #
  # Listed unfiltered on purpose: passing "refs/tags/$TAG" as a pattern drops the "^{}" line, because that is
  # not the ref name being matched. Comparing whole ref names in awk also avoids a pattern like "v0.2.1*"
  # matching v0.2.10.
  REMOTE_REFS="$(git ls-remote --tags origin 2>/dev/null || true)"
  REMOTE_SHA="$(printf '%s\n' "$REMOTE_REFS" | awk -v t="refs/tags/$TAG^{}" '$2 == t {print $1; exit}')"
  [[ -z "$REMOTE_SHA" ]] && REMOTE_SHA="$(printf '%s\n' "$REMOTE_REFS" | awk -v t="refs/tags/$TAG" '$2 == t {print $1; exit}')"

  if [[ -z "$REMOTE_SHA" ]]; then
    echo "ERROR: $TAG is not on origin. Push it first, or the release would be tagged against" >&2
    echo "       whatever the default branch points at:  git push origin $TAG" >&2
    exit 1
  fi
  if [[ "$REMOTE_SHA" != "$HEAD_SHA" ]]; then
    echo "ERROR: origin's $TAG is $REMOTE_SHA but HEAD is $HEAD_SHA." >&2
    echo "       The release would not match the build. Reconcile the tag before publishing." >&2
    exit 1
  fi
  TAG_STATUS=" · verified on origin"
fi

PUBDIR="$ROOT/artifacts/velopack/$RID"
RELEASE_DIR="$ROOT/dist/velopack/$CHANNEL"

echo "==> Bearing release (Velopack)"
echo "    version : $VERSION   (tag $TAG$TAG_STATUS)"
echo "    runtime : $RID   channel $CHANNEL"
echo "    packId  : $PACK_ID"
echo "    output  : $RELEASE_DIR"
echo

# --- Tests (opt out with SKIP_TESTS=1) ----------------------------------------
if [[ "${SKIP_TESTS:-0}" != "1" ]]; then
  echo "==> Running tests"
  dotnet test "$ROOT/Bearing.slnx" -c "$CONFIG" --nologo
  echo
else
  echo "==> Skipping tests (SKIP_TESTS=1)"
  echo
fi

# --- Publish (self-contained directory — deliberately NOT single-file) --------
rm -rf "$PUBDIR"
echo "==> Publishing $PROJECT"
dotnet publish "$PROJECT" \
  -c "$CONFIG" \
  -r "$RID" \
  --self-contained true \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -p:Version="$VERSION" \
  -p:InformationalVersion="$VERSION" \
  -o "$PUBDIR" \
  --nologo
echo

if [[ ! -f "$PUBDIR/$MAIN_EXE" ]]; then
  echo "ERROR: expected published entry point at $PUBDIR/$MAIN_EXE" >&2
  exit 1
fi

# Start from empty and let the feed repopulate it below. vpk reads this directory as the release history,
# so a leftover package from an earlier local run of the same version makes it refuse to pack ("there is a
# release ... equal or greater to the current version") — the history has to come from what is actually
# published, not from what this machine happens to have lying around.
rm -rf "$RELEASE_DIR"
mkdir -p "$RELEASE_DIR"

# --- Previous release, so a delta can be built against it ---------------------
# The repo is public, so reading the feed needs no credential; a token is passed when one is available only
# to stay clear of the 60/hr anonymous API rate limit. Optional by design either way: the first release has
# nothing to diff against, and a failure here must not block building a package — it only costs users a full
# download instead of a delta.
TOKEN="$(gh auth token 2>/dev/null || true)"
echo "==> Fetching the previous $CHANNEL release (for the delta)"
DOWNLOAD_ARGS=(--repoUrl "$REPO_URL" --channel "$CHANNEL" --outputDir "$RELEASE_DIR")
[[ -n "$TOKEN" ]] && DOWNLOAD_ARGS+=(--token "$TOKEN")
vpk download github "${DOWNLOAD_ARGS[@]}" \
  || echo "    none found (or unreachable) — this build will ship as a full package only."
echo

# --- Release notes ------------------------------------------------------------
# A hand-written docs/release-notes/<version>.md wins; otherwise they are derived from the commit subjects
# since the previous tag. Those subjects already carry "(#nn)" refs, which GitHub renders as issue links, so
# the generated notes link back to the issues each release closed without any extra bookkeeping.
NOTES="$ROOT/docs/release-notes/$VERSION.md"
if [[ -f "$NOTES" ]]; then
  echo "==> Release notes: docs/release-notes/$VERSION.md"
else
  NOTES="$ROOT/artifacts/velopack/release-notes-$VERSION.md"
  mkdir -p "$(dirname "$NOTES")"
  # HEAD^ so a tag on HEAD doesn't find itself. Empty on the very first release.
  PREV_TAG="$(git describe --tags --abbrev=0 HEAD^ 2>/dev/null || true)"
  echo "==> Release notes: generated${PREV_TAG:+ from $PREV_TAG..HEAD}"
  {
    if [[ -n "$PREV_TAG" ]]; then
      echo "Changes since ${PREV_TAG}:"
      echo
      git log --no-merges --pretty="- %s" "$PREV_TAG..HEAD"
    else
      echo "First packaged release."
    fi
    echo
    echo "### Install"
    echo
    echo "- **Windows** — \`${PACK_ID}-win-Setup.exe\` (per-user, no admin). Updates itself from here on."
    echo "- **Linux** — \`${PACK_ID}.AppImage\`, \`chmod +x\` and run. Updates itself in place."
    echo
    echo "Unsigned, so Windows SmartScreen warns on first run."
  } > "$NOTES"
fi
echo

# --- Pack ---------------------------------------------------------------------
echo "==> Packing"
EXTRA_PACK_ARGS=()
if [[ "$OS_FAMILY" == "windows" ]]; then
  # Start Menu only, matching what build/release.sh's install.ps1 creates today (no desktop icon).
  EXTRA_PACK_ARGS+=(--shortcuts StartMenuRoot)
else
  # Mirrors the Categories line in release.sh's generated .desktop file.
  EXTRA_PACK_ARGS+=(--categories "Development;Database")
fi

vpk "$DIRECTIVE" pack \
  --packId "$PACK_ID" \
  --packTitle "$PACK_TITLE" \
  --packAuthors "$PACK_AUTHORS" \
  --packVersion "$VERSION" \
  --packDir "$PUBDIR" \
  --mainExe "$MAIN_EXE" \
  --icon "$ICON" \
  --runtime "$RID" \
  --channel "$CHANNEL" \
  --outputDir "$RELEASE_DIR" \
  --releaseNotes "$NOTES" \
  "${EXTRA_PACK_ARGS[@]}"
echo

echo "==> Done"
ls -la "$RELEASE_DIR"
echo

# --- Publish ------------------------------------------------------------------
if [[ "${PUBLISH:-0}" == "1" ]]; then
  if [[ -z "$TOKEN" ]]; then
    echo "ERROR: PUBLISH=1 needs a GitHub token — run 'gh auth login' first." >&2
    exit 1
  fi
  echo "==> Publishing to GitHub Releases ($TAG)"
  # --merge so the other platform's channel can land on the same release: win and linux each carry
  # their own releases.<channel>.json, and the app only ever reads its own.
  vpk upload github \
    --repoUrl "$REPO_URL" --token "$TOKEN" \
    --channel "$CHANNEL" --outputDir "$RELEASE_DIR" \
    --publish --merge \
    --releaseName "Bearing $VERSION" --tag "$TAG"

  # vpk carries the notes inside the package but leaves the GitHub release body to us. Set it here rather
  # than at pack time so it is the same text either platform run produces — last writer wins, same content.
  if command -v gh >/dev/null 2>&1; then
    echo
    echo "==> Setting the release description"
    gh release edit "$TAG" --notes-file "$NOTES" >/dev/null \
      && echo "    done: $(gh release view "$TAG" --json url --jq .url)" \
      || echo "    WARNING: couldn't set the release description; add it by hand." >&2
  fi
else
  echo "Not published (set PUBLISH=1 to upload to GitHub Releases)."
  if [[ "$OS_FAMILY" == "windows" ]]; then
    echo "Install locally with:"
    echo "    $RELEASE_DIR/$PACK_ID-win-Setup.exe"
  else
    echo "Run locally with:"
    echo "    chmod +x $RELEASE_DIR/*.AppImage && $RELEASE_DIR/*.AppImage"
  fi
fi
