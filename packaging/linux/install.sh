#!/usr/bin/env bash
#
# NitroLite — Linux installer.
#
# Installs the published build into the user's home by default, creates the menu entry and the `nitrolite` command,
# and installs the udev rule that lets the app write to the Acer sysfs attributes without running as root.
#
#   ./install.sh                 # ~/.local/share/nitrolite, ~/.local/bin/nitrolite, menu entry, udev rule
#   ./install.sh --no-udev       # same, but leave /etc alone (the app then starts read-only)
#   ./install.sh --system        # /opt/nitrolite, /usr/local/bin, /usr/share/applications
#   ./install.sh --prefix DIR    # install the application files under DIR instead
#
# Nothing here needs root except the udev rule, and that step is skipped cleanly if you decline the prompt.

set -euo pipefail

APP_NAME="NitroLite"
BIN_NAME="NitroLite.Desktop"
DESKTOP_NAME="nitrolite.desktop"
ICON_NAME="nitrolite.svg"
RULE_NAME="99-nitrolite.rules"

MODE="user"
INSTALL_UDEV=1
PREFIX=""

usage() {
    cat <<'USAGE'
Usage: install.sh [options]

  --system        Install for every user (/opt/nitrolite, /usr/local/bin, /usr/share/applications).
  --prefix DIR    Install the application files under DIR instead of the default location.
  --no-udev       Do not install /etc/udev/rules.d/99-nitrolite.rules.
  -h, --help      Show this help.

Without options the install is per-user and needs no root except for the udev rule.
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --system)   MODE="system"; shift ;;
        --prefix)   PREFIX="${2:-}"; [ -n "$PREFIX" ] || { echo "error: --prefix needs a directory" >&2; exit 2; }; shift 2 ;;
        --no-udev)  INSTALL_UDEV=0; shift ;;
        -h|--help)  usage; exit 0 ;;
        *)          echo "error: unknown option '$1'" >&2; usage >&2; exit 2 ;;
    esac
done

# The script is shipped inside the extracted tarball, next to the binary it installs.
SRC_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

# The launcher, the icon and the udev rule live in share/ inside the tarball. They are kept out of the top level
# because "nitrolite.desktop" and "NitroLite.Desktop" are the same file name on a case-insensitive file system, and
# one of them is the application binary. Running the script from a checkout of packaging/linux/ works too.
SHARE_DIR="$SRC_DIR/share"
[ -d "$SHARE_DIR" ] || SHARE_DIR="$SRC_DIR"

if [ ! -f "$SRC_DIR/$BIN_NAME" ]; then
    echo "error: $BIN_NAME was not found next to this script ($SRC_DIR)." >&2
    echo "       Run install.sh from the extracted NitroLite-linux-x64 directory." >&2
    exit 1
fi

if [ "$MODE" = "system" ]; then
    APP_DIR="${PREFIX:-/opt/nitrolite}"
    BIN_DIR="/usr/local/bin"
    DESKTOP_DIR="/usr/share/applications"
    ICON_DIR="/usr/share/icons/hicolor/scalable/apps"
    SUDO="sudo"
else
    APP_DIR="${PREFIX:-$HOME/.local/share/nitrolite}"
    BIN_DIR="$HOME/.local/bin"
    DESKTOP_DIR="$HOME/.local/share/applications"
    ICON_DIR="$HOME/.local/share/icons/hicolor/scalable/apps"
    SUDO=""
fi

echo "Installing $APP_NAME"
echo "  application  $APP_DIR"
echo "  command      $BIN_DIR/nitrolite"
echo "  menu entry   $DESKTOP_DIR/$DESKTOP_NAME"
echo

$SUDO mkdir -p "$APP_DIR"
$SUDO install -m 755 "$SRC_DIR/$BIN_NAME" "$APP_DIR/$BIN_NAME"

# The single-file publish still needs its native libraries beside it: SkiaSharp and HarfBuzz are dlopen'd, not
# embedded, so a copy of the executable on its own would fail to start.
for lib in "$SRC_DIR"/*.so; do
    [ -e "$lib" ] || continue
    $SUDO install -m 644 "$lib" "$APP_DIR/$(basename -- "$lib")"
done

$SUDO mkdir -p "$BIN_DIR"
$SUDO ln -sf "$APP_DIR/$BIN_NAME" "$BIN_DIR/nitrolite"

# Exec and TryExec are rewritten to the absolute path so the entry works even when ~/.local/bin is not on PATH,
# which is still the default on several distributions.
$SUDO mkdir -p "$DESKTOP_DIR"
desktop_tmp="$(mktemp)"
trap 'rm -f "$desktop_tmp"' EXIT
sed -e "s|^Exec=.*|Exec=\"$APP_DIR/$BIN_NAME\"|" \
    -e "s|^TryExec=.*|TryExec=\"$APP_DIR/$BIN_NAME\"|" \
    "$SHARE_DIR/$DESKTOP_NAME" > "$desktop_tmp"
$SUDO install -m 644 "$desktop_tmp" "$DESKTOP_DIR/$DESKTOP_NAME"

if [ -f "$SHARE_DIR/$ICON_NAME" ]; then
    $SUDO mkdir -p "$ICON_DIR"
    $SUDO install -m 644 "$SHARE_DIR/$ICON_NAME" "$ICON_DIR/$ICON_NAME"
fi

# Best effort: a missing cache tool is not a failed install, the desktop environment picks the entry up anyway.
command -v update-desktop-database >/dev/null 2>&1 && \
    $SUDO update-desktop-database "$DESKTOP_DIR" >/dev/null 2>&1 || true

if [ "$INSTALL_UDEV" -eq 1 ] && [ -f "$SHARE_DIR/$RULE_NAME" ]; then
    echo "Installing the udev rule (this is the one step that needs root)..."
    if sudo install -m 644 "$SHARE_DIR/$RULE_NAME" "/etc/udev/rules.d/$RULE_NAME" \
        && sudo udevadm control --reload-rules \
        && sudo udevadm trigger; then
        echo "  udev rule    /etc/udev/rules.d/$RULE_NAME"
        UDEV_OK=1
    else
        echo "  warning: the udev rule was not installed; NitroLite will start in read-only mode." >&2
        echo "           Re-run with sudo, or copy $RULE_NAME to /etc/udev/rules.d/ by hand." >&2
        UDEV_OK=0
    fi
else
    UDEV_OK=0
fi

echo
echo "Done. Start it from the application menu, or run: nitrolite"

case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *) echo "note: $BIN_DIR is not on PATH, so the 'nitrolite' command needs the full path." ;;
esac

if [ "$UDEV_OK" -eq 1 ]; then
    echo
    echo "Log out and back in once: the rule grants an ACL to the user of the current seat, and that ACL is applied"
    echo "when the device is created. Until then the app runs read-only and says so on the settings page."
fi
