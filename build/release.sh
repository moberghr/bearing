#!/usr/bin/env bash
#
# Bearing release builder.
#
# Produces a self-contained, single-file build that runs on a target machine
# WITHOUT the .NET runtime installed, and bundles it into a versioned tarball
# with a local installer, a .desktop launcher, and hicolor icons.
#
# Usage:
#   build/release.sh                       # build linux-x64, version from git
#   VERSION=0.2.0 build/release.sh         # override version
#   RID=linux-arm64 build/release.sh       # override runtime identifier
#   SKIP_TESTS=1 build/release.sh          # skip the test run
#   INSTALL=1 build/release.sh             # install for the current user after building
#   build/release.sh --install             # same, as a flag
#
set -euo pipefail

# --- Arg parsing --------------------------------------------------------------
for arg in "$@"; do
  case "$arg" in
    --install) INSTALL=1 ;;
    *) echo "Unknown argument: $arg" >&2; exit 2 ;;
  esac
done

# --- Locate repo root (this script lives in <root>/build) ---------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

# --- Config -------------------------------------------------------------------
RID="${RID:-linux-x64}"
CONFIG="${CONFIG:-Release}"
PROJECT="src/Bearing.Desktop/Bearing.Desktop.csproj"
APP_ID="bearing"
APP_NAME="Bearing"
DIST="$ROOT/dist"

# Version: explicit VERSION env wins, else derive from git (tag or short sha).
if [[ -z "${VERSION:-}" ]]; then
  if git -C "$ROOT" describe --tags --abbrev=0 >/dev/null 2>&1; then
    VERSION="$(git -C "$ROOT" describe --tags --dirty 2>/dev/null | sed 's/^v//')"
  else
    VERSION="0.1.0+$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo local)"
  fi
fi
# Strip build metadata (+sha) for the assembly Version, which must be numeric.
ASM_VERSION="${VERSION%%+*}"
ASM_VERSION="${ASM_VERSION%%-*}"

echo "==> Bearing release"
echo "    version : $VERSION  (assembly $ASM_VERSION)"
echo "    runtime : $RID"
echo "    config  : $CONFIG"
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

# --- Publish (self-contained, single file) ------------------------------------
PUBDIR="$ROOT/artifacts/publish/$RID"
rm -rf "$PUBDIR"
echo "==> Publishing $PROJECT"
dotnet publish "$PROJECT" \
  -c "$CONFIG" \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -p:Version="$ASM_VERSION" \
  -p:InformationalVersion="$VERSION" \
  -o "$PUBDIR" \
  --nologo
echo

BIN="$PUBDIR/$APP_ID"
if [[ ! -f "$BIN" ]]; then
  echo "ERROR: expected published binary at $BIN" >&2
  exit 1
fi
chmod +x "$BIN"

# --- Stage the distributable tree ---------------------------------------------
STAGE_NAME="${APP_ID}-${VERSION}-${RID}"
STAGE="$ROOT/artifacts/stage/$STAGE_NAME"
rm -rf "$STAGE"
mkdir -p "$STAGE/bin" "$STAGE/share/icons"

cp "$BIN" "$STAGE/bin/$APP_ID"

# Icons: copy every available hicolor size we have PNGs for.
declare -A ICON_SIZES=(
  [16]=tile-16.png   [24]=tile-24.png   [32]=tile-32.png   [48]=tile-48.png
  [64]=tile-64.png   [96]=tile-96.png   [128]=tile-128.png [256]=tile-256.png
  [512]=tile-512.png
)
for sz in "${!ICON_SIZES[@]}"; do
  src="$ROOT/assets/brand/icons/png/${ICON_SIZES[$sz]}"
  [[ -f "$src" ]] && cp "$src" "$STAGE/share/icons/${sz}.png"
done

# --- .desktop launcher (Exec is filled in at install time) --------------------
cat > "$STAGE/share/${APP_ID}.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=$APP_NAME
GenericName=SQL Query Tool
Comment=A fast desktop SQL query tool and script manager for PostgreSQL
Exec=__EXEC__ %U
Icon=$APP_ID
Terminal=false
Categories=Development;Database;
StartupWMClass=$APP_ID
Keywords=sql;postgres;postgresql;database;query;
EOF

# --- Installer (per-user, no root) --------------------------------------------
cat > "$STAGE/install.sh" <<'EOF'
#!/usr/bin/env bash
# Install Bearing for the current user (no root required).
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_ID="bearing"

PREFIX="${PREFIX:-$HOME/.local}"
BIN_DIR="$PREFIX/bin"
ICON_ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor"
APPS_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"

echo "Installing $APP_ID to $PREFIX ..."
install -Dm755 "$HERE/bin/$APP_ID" "$BIN_DIR/$APP_ID"

for png in "$HERE"/share/icons/*.png; do
  sz="$(basename "$png" .png)"
  install -Dm644 "$png" "$ICON_ROOT/${sz}x${sz}/apps/$APP_ID.png"
done

mkdir -p "$APPS_DIR"
sed "s|__EXEC__|$BIN_DIR/$APP_ID|g" "$HERE/share/$APP_ID.desktop" > "$APPS_DIR/$APP_ID.desktop"
chmod 644 "$APPS_DIR/$APP_ID.desktop"

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS_DIR" 2>/dev/null || true
command -v gtk-update-icon-cache   >/dev/null 2>&1 && gtk-update-icon-cache -f -t "$ICON_ROOT" 2>/dev/null || true

echo
echo "Done. Launch from your app menu, or run: $BIN_DIR/$APP_ID"
case ":$PATH:" in
  *":$BIN_DIR:"*) ;;
  *) echo "Note: $BIN_DIR is not on your PATH." ;;
esac
EOF
chmod +x "$STAGE/install.sh"

# --- Uninstaller --------------------------------------------------------------
cat > "$STAGE/uninstall.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
APP_ID="bearing"
PREFIX="${PREFIX:-$HOME/.local}"
ICON_ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor"
APPS_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
rm -f "$PREFIX/bin/$APP_ID"
rm -f "$APPS_DIR/$APP_ID.desktop"
find "$ICON_ROOT" -name "$APP_ID.png" -delete 2>/dev/null || true
echo "Removed $APP_ID (user data under \$XDG_DATA_HOME/bearing was left intact)."
EOF
chmod +x "$STAGE/uninstall.sh"

cp "$ROOT/README.md" "$STAGE/README.md" 2>/dev/null || true

# --- Tarball ------------------------------------------------------------------
mkdir -p "$DIST"
TARBALL="$DIST/${STAGE_NAME}.tar.gz"
rm -f "$TARBALL"
tar -C "$ROOT/artifacts/stage" -czf "$TARBALL" "$STAGE_NAME"

SIZE="$(du -h "$TARBALL" | cut -f1)"
BINSIZE="$(du -h "$BIN" | cut -f1)"

echo "==> Done"
echo "    binary  : $BIN  ($BINSIZE)"
echo "    tarball : $TARBALL  ($SIZE)"
echo
echo "Install locally with:"
echo "    tar xzf $TARBALL -C /tmp"
echo "    /tmp/$STAGE_NAME/install.sh"
echo
echo "Or run the binary directly:"
echo "    $BIN"

# --- Optional install ---------------------------------------------------------
if [[ "${INSTALL:-0}" == "1" ]]; then
  echo
  echo "==> Installing for the current user"
  "$STAGE/install.sh"
fi
