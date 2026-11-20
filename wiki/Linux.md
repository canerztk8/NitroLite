# Linux (experimental)

`NitroLite.Core` holds the platform-independent logic and `NitroLite.Linux` talks to the kernel's own `acer-wmi`
driver, so nothing is reverse-engineered and nothing runs as root. The window is Avalonia (`NitroLite.Desktop`),
which is why the same front end runs on both platforms.

| Feature | Needs |
|---|---|
| Temperatures, fan RPM, fan curve | `acer-wmi` + `hwmon` (in-kernel, nothing to install) |
| Operating modes (Quiet / Balanced / Performance / Turbo) | `/sys/firmware/acpi/platform_profile` |
| Fan speed, CoolBoost, LCD overdrive, backlight timeout, 4-zone RGB | [Linuwu-Sense](https://github.com/0x7375646F/Linuwu-Sense) |
| 80 % charge limit | `charge_control_end_threshold`, or Linuwu-Sense |
| Keyboard lighting when the kernel exposes no RGB node | OpenRGB SDK on `127.0.0.1:6742` |
| Tray background mode, game detection, MUX, School mode, RGB profiles, hardware report | the above, as available |

```bash
tar -xzf NitroLite-linux-x64.tar.gz
cd NitroLite-linux-x64
./install.sh            # ~/.local/share/nitrolite, ~/.local/bin/nitrolite, menu entry, udev rule
./install.sh --system   # /opt/nitrolite, for every user
./install.sh --no-udev  # skip the one step that needs root
```

**The udev rule.** The Acer attributes are owned by root and created `0644`, so a normal user can read them but not
write them. `packaging/linux/99-nitrolite.rules` grants the user of the current seat an ACL on exactly those nodes.
`install.sh` offers to install it; by hand it is:

```bash
sudo install -m 644 99-nitrolite.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo udevadm trigger
```

Log out and back in once: the ACL is applied when the device is created. Until then NitroLite runs read-only and
says so on the settings page, with the command ready to copy.

**The Nitro key.** The laptop's own Nitro key is read from `/dev/input`, which is a different device class from the
Acer attributes and is not covered by the rule above. It needs the `input` group:

```bash
sudo usermod -aG input "$USER"
```

Log out and back in for the group to take effect. Without it the key is simply not heard, the settings row hides
itself and nothing else changes.

`./uninstall.sh` removes the install and keeps your settings; `--purge` deletes those too.

**Status:** the Linux backend is tested against an in-memory `/sys` tree, not against every laptop. The settings
page lists what the probe found on your machine, which is the fastest way to tell whether a missing control is the
kernel's fault or the app's.

---

[< Back to Home](https://github.com/canerztk8/NitroLite/wiki)
