#!/usr/bin/env bash
#
# Bearing release builder.
#
# Produces a self-contained, single-file build that runs on a target machine
# WITHOUT the .NET runtime installed, and bundles it into a versioned archive.
#
#   linux-*  → .tar.gz with a per-user installer, a .desktop launcher and hicolor icons
#   win-*    → .zip with the .exe, its icon, and PowerShell install/uninstall scripts
#
# Usage:
#   build/release.sh                       # build linux-x64, version from git
#   VERSION=0.2.0 build/release.sh         # override version
#   RID=linux-arm64 build/release.sh       # override runtime identifier
#   RID=win-x64 build/release.sh           # cross-build a Windows package from Linux
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

# Target OS family drives packaging (binary suffix, archive format, installer).
case "$RID" in
  linux-*) OS_FAMILY=linux ;;
  win-*)   OS_FAMILY=windows ;;
  osx-*)
    echo "ERROR: macOS packaging is not implemented." >&2
    echo "       A bare Mach-O binary is not usefully distributable (no .app bundle, unsigned," >&2
    echo "       Gatekeeper-quarantined). Publish by hand if you just want a local binary:" >&2
    echo "         dotnet publish $PROJECT -c $CONFIG -r $RID --self-contained true" >&2
    exit 2
    ;;
  *) echo "ERROR: unrecognised RID '$RID' (expected linux-*, win-* or osx-*)." >&2; exit 2 ;;
esac

EXE_SUFFIX=""
[[ "$OS_FAMILY" == "windows" ]] && EXE_SUFFIX=".exe"

# Installing is a host-local operation; you cannot install a Windows build onto Linux.
if [[ "${INSTALL:-0}" == "1" && "$OS_FAMILY" != "linux" ]]; then
  echo "ERROR: --install / INSTALL=1 only applies to linux-* builds on this host." >&2
  echo "       For $RID, copy the archive to the target machine and run its installer there." >&2
  exit 2
fi

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
echo "    runtime : $RID  ($OS_FAMILY)"
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

BIN="$PUBDIR/${APP_ID}${EXE_SUFFIX}"
if [[ ! -f "$BIN" ]]; then
  echo "ERROR: expected published binary at $BIN" >&2
  exit 1
fi
chmod +x "$BIN"

# --- Stage the distributable tree ---------------------------------------------
STAGE_NAME="${APP_ID}-${VERSION}-${RID}"
STAGE="$ROOT/artifacts/stage/$STAGE_NAME"
rm -rf "$STAGE"
mkdir -p "$STAGE"

mkdir -p "$DIST"

if [[ "$OS_FAMILY" == "linux" ]]; then
  # =============================== Linux =====================================
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

  # --- .desktop launcher (Exec is filled in at install time) -----------------
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

  # --- Installer (per-user, no root) -----------------------------------------
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

  # --- Uninstaller ------------------------------------------------------------
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

  # --- Tarball ----------------------------------------------------------------
  ARCHIVE="$DIST/${STAGE_NAME}.tar.gz"
  rm -f "$ARCHIVE"
  tar -C "$ROOT/artifacts/stage" -czf "$ARCHIVE" "$STAGE_NAME"

  INSTALL_HINT="    tar xzf $ARCHIVE -C /tmp
    /tmp/$STAGE_NAME/install.sh"

else
  # ============================== Windows ====================================
  # Flat layout: the .exe is self-contained, so the zip is the .exe plus its icon
  # and the scripts that wire up a Start Menu entry.
  command -v zip >/dev/null 2>&1 || {
    echo "ERROR: 'zip' is required to package a Windows build. Install it (dnf install zip)." >&2
    exit 1
  }

  cp "$BIN" "$STAGE/${APP_ID}.exe"
  cp "$ROOT/assets/brand/icons/${APP_ID}.ico" "$STAGE/${APP_ID}.ico"

  # --- Installer (per-user, no admin) ----------------------------------------
  cat > "$STAGE/install.ps1" <<'EOF'
#Requires -Version 5.1
# Install Bearing for the current user (no administrator rights required).
$ErrorActionPreference = 'Stop'

$AppId   = 'bearing'
$AppName = 'Bearing'
$Target  = Join-Path $env:LOCALAPPDATA "Programs\$AppId"

Write-Host "Installing $AppId to $Target ..."
New-Item -ItemType Directory -Force -Path $Target | Out-Null
Copy-Item (Join-Path $PSScriptRoot "$AppId.exe") -Destination $Target -Force
Copy-Item (Join-Path $PSScriptRoot "$AppId.ico") -Destination $Target -Force

# Start Menu shortcut.
$Programs = [Environment]::GetFolderPath('Programs')
$Link     = Join-Path $Programs "$AppName.lnk"
$Shell    = New-Object -ComObject WScript.Shell
$Shortcut = $Shell.CreateShortcut($Link)
$Shortcut.TargetPath       = Join-Path $Target "$AppId.exe"
$Shortcut.WorkingDirectory = $Target
$Shortcut.IconLocation     = Join-Path $Target "$AppId.ico"
$Shortcut.Description      = 'A fast desktop SQL query tool and script manager for PostgreSQL'
$Shortcut.Save()

Write-Host ""
Write-Host "Done. Launch from the Start Menu, or run:"
Write-Host "  $(Join-Path $Target "$AppId.exe")"
Write-Host ""
Write-Host "Settings live in %APPDATA%\$AppId, data (query log, secrets) in %LOCALAPPDATA%\$AppId."
EOF

  # --- Uninstaller ------------------------------------------------------------
  cat > "$STAGE/uninstall.ps1" <<'EOF'
#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

$AppId   = 'bearing'
$AppName = 'Bearing'
$Target  = Join-Path $env:LOCALAPPDATA "Programs\$AppId"
$Link    = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"

if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
if (Test-Path $Link)   { Remove-Item $Link -Force }

Write-Host "Removed $AppId. User data in %APPDATA%\$AppId and %LOCALAPPDATA%\$AppId was left intact."
EOF

  cp "$ROOT/README.md" "$STAGE/README.md" 2>/dev/null || true

  # --- Zip --------------------------------------------------------------------
  ARCHIVE="$DIST/${STAGE_NAME}.zip"
  rm -f "$ARCHIVE"
  (cd "$ROOT/artifacts/stage" && zip -rq "$ARCHIVE" "$STAGE_NAME")

  INSTALL_HINT="    Copy $(basename "$ARCHIVE") to the Windows machine, extract it, then in PowerShell:
      cd <extracted>\\$STAGE_NAME
      powershell -ExecutionPolicy Bypass -File .\\install.ps1"
fi

SIZE="$(du -h "$ARCHIVE" | cut -f1)"
BINSIZE="$(du -h "$BIN" | cut -f1)"

echo "==> Done"
echo "    binary  : $BIN  ($BINSIZE)"
echo "    archive : $ARCHIVE  ($SIZE)"
echo
echo "Install with:"
echo "$INSTALL_HINT"
echo
echo "Or run the binary directly:"
echo "    $BIN"

# --- Optional install ---------------------------------------------------------
if [[ "${INSTALL:-0}" == "1" ]]; then
  echo
  echo "==> Installing for the current user"
  "$STAGE/install.sh"
fi
