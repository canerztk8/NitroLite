#!/bin/sh
# NitroLite package: before removal. Runs as root, from dpkg or rpm.
#
# On an upgrade the package manager also runs this; the socket is only stopped for a real removal, so an upgrade does
# not leave the app without its helper. dpkg passes "remove"/"upgrade", rpm passes the number of versions that will
# remain (0 on removal).

case "${1:-}" in
    remove|purge|0)
        if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then
            systemctl disable --now nitrolite-helper.socket nitrolite-helper.service || true
        fi
        ;;
esac

exit 0
