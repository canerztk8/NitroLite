#!/usr/bin/env bash
#
# NitroLite — Linux uninstaller.
#
# Removes what install.sh created. Settings are left alone unless --purge is given, because a reinstall should not
# cost the user their fan curve and language choice.
#
#   ./uninstall.sh               # remove the per-user install
#   ./uninstall.sh --system      # remove the /opt install
#   ./uninstall.sh --purge       # also remove ~/.local/share/NitroLite (settings, logs, profiles.user.json)
#   ./uninstall.sh --keep-udev   # leave /etc/udev/rules.d/99-nitrolite.rules in place

set -euo pipefail

DESKTOP_NAME="nitrolite.desktop"
ICON_NAME="nitrolite.svg"
RULE_NAME="99-nitrolite.rules"

MODE="user"
PURGE=0
REMOVE_UDEV=1
PREFIX=""

usage() {
    cat <<'USAGE'
Usage: uninstall.sh [options]

  --system      Remove the install made with install.sh --system.
  --prefix DIR  Remove an install made with install.sh --prefix DIR.
  --purge       Also delete the settings and log folder (~/.local/share/NitroLite).
  --keep-udev   Leave /etc/udev/rules.d/99-nitrolite.rules in place.
  -h, --help    Show this help.
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --system)    MODE="system"; shift ;;
        --prefix)    PREFIX="${2:-}"; [ -n "$PREFIX" ] || { echo "error: --prefix needs a directory" >&2; exit 2; }; shift 2 ;;
        --purge)     PURGE=1; shift ;;
        --keep-udev) REMOVE_UDEV=0; shift ;;
        -h|--help)   usage; exit 0 ;;
        *)           echo "error: unknown option '$1'" >&2; usage >&2; exit 2 ;;
    esac
done

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

echo "Removing NitroLite"

# --prefix is a path the user types, and the next step is `rm -rf` on it. A typo — `--prefix /`, `--prefix /usr` —
# would take the system with it, so the directory has to look like a NitroLite install before anything is removed:
# the launcher has to be there and executable, and the path has to be neither a system directory nor one of its
# ancestors.
case "$APP_DIR" in
    /|/bin|/boot|/dev|/etc|/home|/lib|/lib32|/lib64|/opt|/proc|/root|/run|/sbin|/srv|/sys|/tmp|/usr|/var|\
    /usr/bin|/usr/lib|/usr/lib64|/usr/local|/usr/local/bin|/usr/sbin|/usr/share|/var/lib|/var/tmp)
        echo "error: refusing to remove '$APP_DIR': that is a system directory, not a NitroLite install." >&2
        exit 2
        ;;
esac

if [ ! -x "$APP_DIR/NitroLite.Desktop" ]; then
    echo "error: '$APP_DIR' does not look like a NitroLite install (no NitroLite.Desktop in it)." >&2
    echo "       Nothing was removed. Pass the folder install.sh used, or delete it by hand." >&2
    exit 2
fi

# The symlink goes first: removing the directory it points at would leave a dangling command on PATH.
if [ -L "$BIN_DIR/nitrolite" ] || [ -f "$BIN_DIR/nitrolite" ]; then
    $SUDO rm -f "$BIN_DIR/nitrolite"
    echo "  removed      $BIN_DIR/nitrolite"
fi

if [ -d "$APP_DIR" ]; then
    $SUDO rm -rf "$APP_DIR"
    echo "  removed      $APP_DIR"
fi

if [ -f "$DESKTOP_DIR/$DESKTOP_NAME" ]; then
    $SUDO rm -f "$DESKTOP_DIR/$DESKTOP_NAME"
    echo "  removed      $DESKTOP_DIR/$DESKTOP_NAME"
fi

if [ -f "$ICON_DIR/$ICON_NAME" ]; then
    $SUDO rm -f "$ICON_DIR/$ICON_NAME"
    echo "  removed      $ICON_DIR/$ICON_NAME"
fi

command -v update-desktop-database >/dev/null 2>&1 && \
    $SUDO update-desktop-database "$DESKTOP_DIR" >/dev/null 2>&1 || true

if [ "$MODE" = "system" ] && [ -f /etc/systemd/system/nitrolite-helper.socket ]; then
    echo "Removing the NitroLite helper service (needs root)..."
    sudo systemctl disable --now nitrolite-helper.socket nitrolite-helper.service >/dev/null 2>&1 || true
    sudo rm -f /etc/systemd/system/nitrolite-helper.socket /etc/systemd/system/nitrolite-helper.service         /usr/share/polkit-1/actions/io.github.canerztk8.nitrolite.policy
    sudo systemctl daemon-reload >/dev/null 2>&1 || true
fi

if [ "$REMOVE_UDEV" -eq 1 ] && [ -f "/etc/udev/rules.d/$RULE_NAME" ]; then
    echo "Removing the udev rule (needs root)..."
    if sudo rm -f "/etc/udev/rules.d/$RULE_NAME" \
        && sudo udevadm control --reload-rules \
        && sudo udevadm trigger; then
        echo "  removed      /etc/udev/rules.d/$RULE_NAME"
    else
        echo "  warning: /etc/udev/rules.d/$RULE_NAME is still there." >&2
    fi
fi

if [ "$PURGE" -eq 1 ]; then
    DATA_DIR="$HOME/.local/share/NitroLite"
    if [ -d "$DATA_DIR" ]; then
        rm -rf "$DATA_DIR"
        echo "  removed      $DATA_DIR"
    fi
else
    echo "  kept         $HOME/.local/share/NitroLite (settings and logs; use --purge to delete)"
fi

echo
echo "Done."
