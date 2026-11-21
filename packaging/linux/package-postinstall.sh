#!/bin/sh
# NitroLite package: after install or upgrade. Runs as root, from dpkg or rpm.
#
# Starts listening on the helper's socket (the helper itself only starts when the app connects), and applies the udev
# rule to the devices that already exist, so the keyboard controller is usable without a reboot. Every step is best
# effort: a container or a chroot without systemd must still install the package.

if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then
    systemctl daemon-reload || true
    systemctl enable --now nitrolite-helper.socket || true
    # An upgrade replaces the binary under a helper that may still be running the old one.
    systemctl try-restart nitrolite-helper.service || true
fi

if command -v udevadm >/dev/null 2>&1; then
    udevadm control --reload-rules || true
    udevadm trigger --subsystem-match=hidraw --subsystem-match=hwmon --subsystem-match=power_supply \
        --subsystem-match=platform || true
fi

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q /usr/share/icons/hicolor || true

exit 0
