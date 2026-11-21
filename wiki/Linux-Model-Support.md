# Linux model support

Which Acer Nitro and Predator laptops the Linux kernel already supports, and from which release. On a listed model
with a new enough kernel, NitroLite gets the operating modes, the fan sensors and (where marked) fan control with
nothing installed. NitroLite reads this table too: on a listed model it tells you exactly which kernel brings what.

## Supported by the mainline kernel

| Model | Modes and fan RPM since | Fan control since | Notes |
|---|---|---|---|
| Nitro AN515-58 | Linux 6.14 | Linux 6.19 | |
| **Nitro AN16-41** | **Linux 7.4** | **Linux 7.4** | Tested by the kernel patch's author: five modes, both fans, manual / max / auto fan control |
| Nitro ANV15-51 | Linux 7.4 | Linux 7.4 | The firmware rejects the *performance* profile; the other four modes work |
| Predator PH315-53 | Linux 5.15 | — | Turbo key and fan mode only |
| Predator PHN16-71 | Linux 6.8 | — | |
| Predator PH16-71 | Linux 6.9 | — | |
| Predator PH18-71 | Linux 6.9 | — | |
| Predator PH16-72 | Linux 6.14 | Linux 6.19 | |
| Predator PHN16-72 (Helios Neo 16) | Linux 6.19 | Linux 6.19 | |
| Predator PT14-51 (Triton 14) | Linux 6.14 | Linux 6.19 | |

Source: the model table of the kernel's `acer-wmi` driver (`drivers/platform/x86/acer-wmi.c`) as of 21 November 2026,
with each entry dated by the first release tag that contains it. Linux 7.4 was at release candidate 3 at that date.
Distributions ship new kernels on their own schedule, so "since Linux 7.4" means the first distribution release
with 7.4 or newer.

Find your kernel with `uname -r`.

## My model is listed, but my kernel is older

Turn on the `predator_v4` option. It gives the same modes and fan RPM on older kernels; fan control still needs the
release in the table.

**Settings → Linux setup → Turn on predator_v4**, then restart. By hand:

```bash
echo "options acer_wmi predator_v4=1" | sudo tee /etc/modprobe.d/nitrolite-acer.conf
```

Delete that file and restart to undo it. Once your distribution moves to the listed kernel, you can delete it too.

## My model is not listed

Your laptop may still work: many recent Nitro and Predator models use the same firmware interface as the listed ones,
which is what `predator_v4` switches on.

1. **Try `predator_v4`** as above. If modes and fan RPM appear, your model works the same way as the listed ones.
2. **Tell us**: **Settings → Linux setup → Send Linux info**. NitroLite records what it reads (no serial numbers, host
   name or user name), opens the folder with the file, and opens a GitHub issue; drag the file in. If `predator_v4`
   worked, say so in the issue. That is the evidence needed to add your model to the kernel, so the next person with
   your laptop gets it with no extra step.
3. **For fan speed, keyboard colour and the charge limit** without kernel support, there are two out-of-tree
   modules: [acer-wmi-linuwu](https://github.com/adityaruplaha/acer-wmi-linuwu), which extends the kernel's own
   driver, and [Linuwu-Sense](https://github.com/0x7375646F/Linuwu-Sense), whose authors report testing on kernels
   6.12 to 6.14 (it may not build on newer ones). The 80 % charge limit is in the kernel itself from Linux 7.3 on the
   models its `acer-wmi-battery` driver lists.

## Keyboard colour

The kernel has no keyboard lighting support for these models. Two ways to get it:

| Keyboard | How NitroLite reaches it |
|---|---|
| ENE KB5130 controller (USB `0cf2:5130`): 2024 and later Nitro V, Nitro 16S, Helios Neo boards | directly, through `/dev/hidraw`, after **Enable** in Settings |
| Four-zone keyboards driven by the firmware (most other Nitro and Predator models, including the AN16-41) | Linuwu-Sense, or OpenRGB if it supports your keyboard |

`lsusb | grep 0cf2:5130` tells you whether you have the KB5130.

## What we know from real machines

| Laptop | Distribution, kernel | Result |
|---|---|---|
| Nitro AN16-41 (BIOS V1.27) | Ubuntu 26.04.1, Linux 7.0 | Before 7.4: modes through `amd-pmf`, no fan RPM, `predator_v4` available and off. No KB5130 keyboard |

A capture sent through **Send Linux info** adds a row here, and can be added to NitroLite's test suite so that a later
version cannot break that model without the build failing.

---

[< Back to Linux](https://github.com/canerztk8/NitroLite/wiki/Linux) · [Home](https://github.com/canerztk8/NitroLite/wiki)
