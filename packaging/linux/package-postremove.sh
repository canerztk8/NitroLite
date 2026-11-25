#!/bin/sh
# NitroLite package: after removal. Runs as root, from dpkg or rpm. Forgets the units the package took away.

if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then
    systemctl daemon-reload || true
fi

# On purge (dpkg "purge"; rpm has no purge, so its full erase, 0 versions left; never on upgrade or a plain dpkg
# "remove"), the two files the app itself writes under /etc go too. Neither belongs to the package: the predator_v4
# option is written by the settings page, and /etc/udev/rules.d/99-nitrolite.rules by the in-app "Enable" (grant)
# button or install.sh. The package's own rule under /usr/lib went with the package.
case "${1:-}" in
    purge|0)
        rm -f /etc/modprobe.d/nitrolite-acer.conf /etc/udev/rules.d/99-nitrolite.rules || true
        ;;
esac

if command -v udevadm >/dev/null 2>&1; then
    udevadm control --reload-rules || true
fi

exit 0
