# Linux

NitroLite runs on Linux with the same window as on Windows. It talks to the hardware through what the kernel already
offers, so on a normal desktop distribution (Ubuntu, Fedora, Mint, Pop!_OS, Arch…) most of it works with nothing
extra installed. What your laptop can do depends on your kernel; NitroLite shows only the controls that will work and
says why the others are missing.

## Install

### The easy way: a package

From the **[Releases](https://github.com/canerztk8/NitroLite/releases/latest)** page:

| Distribution | Download | If double-clicking does not install it |
|---|---|---|
| Ubuntu, Debian, Mint, Pop!_OS, Zorin, elementary | `NitroLite-v…-amd64.deb` | `sudo apt install ./NitroLite-v…-amd64.deb` |
| Fedora, openSUSE | `NitroLite-v…-x86_64.rpm` | `sudo dnf install ./NitroLite-v…-x86_64.rpm` |

Open NitroLite from the menu. That is the whole setup.

The package installs a small **helper service** next to the app. When NitroLite changes the operating mode, the fans,
the keyboard or the charge limit, the helper makes the change; your desktop's permission system (polkit) lets the
person sitting at the laptop do that without a password, and nobody else. There are no groups to join and no need to
log out. The helper only starts when NitroLite asks for something and stops again half a minute later, it can only
write the handful of hardware settings NitroLite uses, and only values each of them accepts.

To remove it: `sudo apt remove nitrolite` or `sudo dnf remove nitrolite`.

### Any other distribution: the archive

```bash
tar -xzf NitroLite-v…-linux-x64.tar.gz
cd NitroLite-linux-x64
./install.sh --system
```

| Option | What it does |
|---|---|
| `./install.sh --system` | Installs for every user (`/opt/nitrolite`) and, on systemd distributions, the same helper service as the packages |
| `./install.sh` | Installs for you only (`~/.local/share/nitrolite`), without the helper: a root service must not run a program from your home folder |
| `./install.sh --no-udev` | Skips the permission rule |
| `./uninstall.sh` | Removes NitroLite (add `--system` for a system install) and keeps your settings; `--purge` removes those too |

## Without the helper: one button

On a per-user install, or a distribution without systemd, NitroLite uses a permission rule instead. Open
**Settings**: the **Linux setup** card has an **Enable** button. It asks for your password once, then

- installs the permission rule (`99-nitrolite.rules`) so NitroLite may change the operating mode, the keyboard, the
  charge limit and the processor boost without running as root,
- adds you to the `nitrolite` group, which owns those settings, and to the `input` group, which lets NitroLite hear
  the laptop's **Nitro key**.

**Log out and back in afterwards.** Group membership is only read at login.

The rule does **not** hand over the fans. A fan speed written straight to the kernel can be 0, which stops the fan,
and nothing would put the fans back in automatic if NitroLite were killed. Fan control goes through the helper, which
refuses a speed below NitroLite's floor and returns the fans to the firmware when NitroLite exits; on an install
without the helper, fan control is not available.

You can skip it. The operating mode still works through your desktop's own power service, and temperatures, the
graphics card and battery health need no permission at all.

## What works

| Feature | Works out of the box? | Needs |
|---|---|---|
| CPU temperature, load, clock, RAM | Yes | — |
| NVIDIA GPU temperature, load, clock, power, VRAM | Yes | the NVIDIA driver (`nvidia-smi` ships with it) |
| Battery health and charge state | Yes | — |
| Operating modes (Quiet / Balanced / Performance) | Yes | power-profiles-daemon or tuned-ppd, which most desktops include. With the permission rule, NitroLite writes the kernel's own list directly, which can be finer |
| School mode: screen brightness | Yes | your desktop session (`systemd-logind`) |
| School mode: processor boost off | After **Enable** | the permission rule |
| Nitro key | After **Enable** | the `input` group |
| Keyboard colour on the 2024+ boards (Nitro V, Nitro 16S, Helios Neo with an ENE KB5130 controller) | After **Enable** | the permission rule |
| Fan RPM and Acer's own modes | Depends on the model | the kernel knowing your model; see **[Linux model support](https://github.com/canerztk8/NitroLite/wiki/Linux-Model-Support)**, or `predator_v4` |
| Fan speed and fan curve | On the models the kernel lists for fan control | the helper (the packages, or `install.sh --system`) and a new enough kernel ([model support](https://github.com/canerztk8/NitroLite/wiki/Linux-Model-Support)), or an out-of-tree module |
| 80 % charge limit | On the models the kernel lists, from Linux 7.3 | the kernel's `acer-wmi-battery` driver (Nitro ANV15-51, AN515-57, AN515-58, AN517-54 and some Aspire models), or an out-of-tree module |
| 4-zone keyboard colour (firmware-driven boards), backlight timeout | No | an out-of-tree module: [acer-wmi-linuwu](https://github.com/adityaruplaha/acer-wmi-linuwu) or [Linuwu-Sense](https://github.com/0x7375646F/Linuwu-Sense) |
| CoolBoost, Windows key lock | No | [acer-wmi-linuwu](https://github.com/adityaruplaha/acer-wmi-linuwu), on the models it lists (AN515-57) |
| GPU mode (MUX) | No | no Linux driver exposes it; switch it in the firmware setup (BIOS) instead |
| Keyboard colour through OpenRGB | If OpenRGB is running | the OpenRGB SDK server on `127.0.0.1:6742` |

The **Settings** page lists what was found on your machine. That is the quickest way to tell whether a missing
control is your kernel or NitroLite.

## Unlock more

First check **[Linux model support](https://github.com/canerztk8/NitroLite/wiki/Linux-Model-Support)**: if your model is
listed, a newer kernel may already do everything below for you. NitroLite also says so on the Settings page.

### Fan RPM and Acer modes: `predator_v4` (experimental)

The kernel's `acer-wmi` driver turns on the Nitro/Predator features only for models on its list. If your model is not
listed yet, NitroLite offers to switch them on: **Settings → Linux setup → Turn on predator_v4**, then restart the
computer. By hand:

```bash
echo "options acer_wmi predator_v4=1" | sudo tee /etc/modprobe.d/nitrolite-acer.conf
```

To undo it, delete that file and restart. If it works on your laptop, please
**[open an issue](https://github.com/canerztk8/NitroLite/issues)** with your model: that is the evidence needed to add
the model to the kernel, after which nobody with that laptop has to do this step.

On AMD laptops the `amd-pmf` driver usually owns the operating mode already. With `predator_v4` on, `acer-wmi` adds a
second provider, and the kernel then offers only the modes both agree on, so the list of modes may change.

### Fan speed, keyboard colour and charge limit: an out-of-tree module

On a model the kernel does not cover yet, these controls need a kernel module that is not part of Linux. There are two,
and NitroLite finds either on the next start:

- **[acer-wmi-linuwu](https://github.com/adityaruplaha/acer-wmi-linuwu)** replaces the kernel's own `acer-wmi` with
  an extended copy through DKMS, so nothing is blacklisted and everything the kernel driver did keeps working. It is
  the only one with CoolBoost and the Windows key lock.
- **[Linuwu-Sense](https://github.com/0x7375646F/Linuwu-Sense)** blacklists `acer-wmi` and loads its own module in its
  place.

Install one, not both, following its own instructions. If **Secure Boot** is on, the module has to be signed, which
both cover. NitroLite does not install kernel modules itself.

## Doing it by hand

What **Enable** does, if you prefer a terminal or your desktop has no password dialog (`pkexec`):

```bash
sudo groupadd -f nitrolite
sudo usermod -aG nitrolite,input "$USER"
sudo install -m 644 99-nitrolite.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo udevadm trigger
```

The rule file is in the `share` folder of the download, and NitroLite also writes a copy into its data folder. Log
out and back in afterwards. The Settings page shows the same command with the right path, ready to copy.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| The mode switch does nothing and the Settings page shows the Linux setup card | no permission and no power-profile service | press **Enable**, or install `power-profiles-daemon` |
| The Nitro key is not heard | not in the `input` group | **Enable**, then log out and back in |
| The keyboard page is covered with "Keyboard lighting is not reachable" | the kernel offers no path to your keyboard | acer-wmi-linuwu or Linuwu-Sense, or OpenRGB |
| No fan RPM | `acer-wmi` does not know your model | try `predator_v4` |
| The graphics card shows no reading | the GPU is asleep, or the NVIDIA driver is not installed | open a game or `nvidia-smi` once; a sleeping GPU is not woken on purpose, to save battery |
| No NitroLite icon in the top bar, and closing the window only minimises it | GNOME shows no tray icons without the AppIndicator extension (Ubuntu ships it; Fedora and Debian do not) | install "AppIndicator and KStatusNotifierItem Support" from GNOME Extensions; until then NitroLite keeps running minimised in the dock instead of disappearing |
| The on-screen display does not appear over a game | the game runs natively on Wayland in fullscreen, where no application can draw on top | most Steam and Proton games run through XWayland and show it; for a native Wayland game, play it windowed or borderless |

Still stuck? **Settings → Export hardware report** and attach the file to a
**[GitHub issue](https://github.com/canerztk8/NitroLite/issues)**.

## Tested on

| Laptop | Distribution | Result |
|---|---|---|
| Acer Nitro 16 **AN16-41** (Ryzen 7 7840HS, RTX 4070) | Ubuntu 26.04.1, kernel 7.0 | From a capture of the machine: the operating mode (through `amd-pmf`), temperatures, the RTX 4070 and battery health are all readable, and the mode is writable once the permission rule is in. No fan RPM: `predator_v4` is off and not yet tried. Fan speed and keyboard colour need Linuwu-Sense, because this keyboard has no KB5130 controller. The 1.3.0 build itself has not been run on this laptop yet |

Other models have not been run on real hardware yet. **Settings → Linux setup → Send Linux info** records your
laptop and opens a GitHub issue for it: the most useful thing you can send. The Linux 7.4 kernel adds full support
for the AN16-41, fan control included.

## For contributors: capture a laptop once

Working on the Linux build should not mean rebooting into Linux for every change. On the Linux laptop, once:

```bash
wget https://raw.githubusercontent.com/canerztk8/NitroLite/main/tools/linux-capture/nitrolite-capture.sh
sh nitrolite-capture.sh
```

It writes `nitrolite-capture-<date>.txt`. The script only reads: it records the `/sys` and `/proc` files NitroLite
uses, which of them are writable, and what `busctl`, `powerprofilesctl` and `nvidia-smi` answer. It does not record
serial numbers, MAC addresses, the host name or the user name.

Then, on any machine, Windows included:

```bash
dotnet run --project NitroLite.Desktop -f net10.0 -- --linux-capture nitrolite-capture-<date>.txt
```

That opens the Linux window driven by the recorded laptop, with its own data folder, beside an installed NitroLite.
The same file loads in a unit test with `LinuxCapture.LoadFile`. Details in
[`tools/linux-capture`](https://github.com/canerztk8/NitroLite/tree/main/tools/linux-capture).

## How it works

`NitroLite.Core` holds the platform-independent logic and `NitroLite.Linux` the Linux backend; the window is the same
Avalonia front end on both systems. The backend reads `/sys` (`hwmon`, `platform_profile`, `power_supply`, `hidraw`,
`acer-wmi` and Linuwu-Sense), asks power-profiles-daemon over D-Bus through `busctl`, reads NVIDIA through
`nvidia-smi`, and sets the backlight through `systemd-logind`. Nothing runs as root. Every write is read back before it
is reported as done, and a write refused for lack of permission is reported as such rather than as a hardware fault.
The full file list is on the **[Architecture](https://github.com/canerztk8/NitroLite/wiki/Architecture)** page.

---

[< Back to Home](https://github.com/canerztk8/NitroLite/wiki)
