#!/bin/sh
# NitroLite package: after removal. Runs as root, from dpkg or rpm. Forgets the units the package took away.

if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then
    systemctl daemon-reload || true
fi

if command -v udevadm >/dev/null 2>&1; then
    udevadm control --reload-rules || true
fi

exit 0
