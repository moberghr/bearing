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

if [[ "${PUBLISH:-0}" == "1" && "${ALLOW_UNTAGGED:-0}" == "1" ]]; then
  echo "ERROR: refusing to PUBLISH an untagged build." >&2
  exit 1
fi

PUBDIR="$ROOT/artifacts/velopack/$RID"
RELEASE_DIR="$ROOT/dist/velopack/$CHANNEL"

echo "==> Bearing release (Velopack)"
echo "    version : $VERSION   (tag $TAG)"
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
# Optional by design: the first release has nothing to diff against, and a token problem here must not
# block building a package — it only costs users a full download.
if command -v gh >/dev/null 2>&1 && TOKEN="$(gh auth token 2>/dev/null)" && [[ -n "$TOKEN" ]]; then
  echo "==> Fetching the previous $CHANNEL release (for the delta)"
  vpk download github \
    --repoUrl "$REPO_URL" --token "$TOKEN" \
    --channel "$CHANNEL" --outputDir "$RELEASE_DIR" \
    || echo "    none found (or unreachable) — this build will ship as a full package only."
  echo
else
  TOKEN=""
  echo "==> No gh token; skipping the previous-release fetch (no delta will be built)."
  echo
fi

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
