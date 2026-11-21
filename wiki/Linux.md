# Linux (experimental)

`NitroLite.Core` holds the platform-independent logic and `NitroLite.Linux` talks to the kernel's own `acer-wmi`
driver, so nothing is reverse-engineered and nothing runs as root. The window is Avalonia (`NitroLite.Desktop`),
which is why the same front end runs on both platforms.

| Feature | Needs |
|---|---|
| CPU temperature, load, clocks, RAM | `hwmon` and `/proc` (nothing to install) |
| NVIDIA GPU temperature, load, clock, power, VRAM | `nvidia-smi`, which ships with the NVIDIA driver |
| Operating modes (Quiet / Balanced / Performance) | power-profiles-daemon or tuned-ppd, which most desktops ship; finer modes with the udev rule |
| Fan RPM, Acer modes | `acer-wmi` + `hwmon`; on models the driver does not list, the `predator_v4` option (the Settings page offers it) |
| Keyboard RGB on the KB5130 boards (Nitro V / 16S, Helios Neo 2024+) | `/dev/hidraw`, with the udev rule |
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

**The Settings page does the rest.** When something needs permission, the *Linux setup* card has one *Enable* button:
it asks for your password once (through `pkexec`), installs the udev rule, and adds you to the `nitrolite` and `input`
groups. Log out and back in afterwards. The same card offers the `predator_v4` option when your kernel has it switched
off, and points at Linuwu-Sense for the controls only that module can reach. What follows is the manual route.

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
