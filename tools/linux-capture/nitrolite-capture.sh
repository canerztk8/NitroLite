#!/bin/sh
# NitroLite — one-shot Linux hardware capture.
#
# Records every /sys and /proc file the NitroLite Linux backend reads, which of them this user may write, and the
# output of the few tools it runs (busctl, powerprofilesctl, nvidia-smi), into one text file. The file is replayed on
# a development machine — on Windows too — so the Linux build can be worked on without booting Linux each time:
#
#   NitroLite.Desktop --linux-capture nitrolite-capture-*.txt     (the Linux window, driven by this machine's data)
#   LinuxCapture.Load(...)                                         (the same data in a unit test)
#
# It is read-only. It writes nothing to /sys, needs no root, and installs nothing. It does not record serial numbers,
# the machine UUID, MAC addresses, the host name, the user name or the home folder: the DMI serial files are never
# read, the input device list drops its Uniq= lines, and the host and user names are replaced in the finished file.
#
# Usage:  sh nitrolite-capture.sh [output-file]

set -u
LC_ALL=C
export LC_ALL

OUT="${1:-nitrolite-capture-$(date +%Y%m%d-%H%M%S).txt}"
TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT

emit() { printf '%s\n' "$*" >>"$TMP"; }

# One regular file: its contents, each line prefixed with "| " so no content line can look like a marker. A file this
# user cannot read is left out entirely, which is what the app sees too.
file() {
    p="$1"
    [ -f "$p" ] || return 0
    content="$(timeout 2 head -c 65536 "$p" 2>/dev/null)" || return 0
    emit "@@ file $p"
    printf '%s\n' "$content" | sed 's/^/| /' >>"$TMP"
    [ -w "$p" ] || emit "@@ ro $p"
}

# A character device the app opens (hidraw): recorded as an empty file, with its writability.
node() {
    p="$1"
    [ -e "$p" ] || return 0
    emit "@@ file $p"
    [ -w "$p" ] || emit "@@ ro $p"
}

dir() { [ -d "$1" ] && emit "@@ dir $1"; return 0; }

# A binary file (a HID report descriptor) as one line of hex.
hex() {
    p="$1"
    [ -r "$p" ] || return 0
    emit "@@ hex $p"
    printf '| %s\n' "$(od -An -v -tx1 "$p" | tr -d ' \n')" >>"$TMP"
}

# Every attribute directly inside a sysfs device directory, one level of subdirectories deep. Links back into the
# device tree (driver, subsystem, power, firmware_node) are skipped: they lead to thousands of unrelated files.
attrs() {
    d="$1"
    [ -d "$d" ] || return 0
    emit "@@ dir $d"
    for f in "$d"/*; do
        name="${f##*/}"
        case "$name" in driver|subsystem|power|firmware_node|modalias|uevent) continue ;; esac
        if [ -f "$f" ]; then
            file "$f"
        elif [ -d "$f" ] && [ ! -L "$f" ]; then
            emit "@@ dir $f"
            for g in "$f"/*; do [ -f "$g" ] && file "$g"; done
        fi
    done
}

# A command, recorded exactly as the app runs it, with its exit code and standard output.
cmd() {
    tool="$1"
    path="$(command -v "$tool" 2>/dev/null)"
    if [ -z "$path" ]; then
        emit "@@ which $tool absent"
        return 0
    fi
    emit "@@ which $tool present"
    out="$(timeout 5 "$@" 2>/dev/null)"
    code=$?
    emit "@@ cmd $*"
    emit "@@ exit $code"
    printf '%s\n' "$out" | sed 's/^/| /' >>"$TMP"
}

emit "NITROLITE-CAPTURE 1"
emit "@@ meta captured $(date -u +%Y-%m-%dT%H:%M:%SZ)"

# --- identity: the model fields only; the DMI serial and UUID files are never read
for f in sys_vendor product_name product_family product_version board_name board_vendor bios_version bios_vendor bios_date; do
    file "/sys/class/dmi/id/$f"
done
file /etc/os-release
file /proc/sys/kernel/osrelease
file /proc/cpuinfo
file /proc/meminfo
file /proc/stat

# --- operating mode
file /sys/firmware/acpi/platform_profile
file /sys/firmware/acpi/platform_profile_choices
for d in /sys/class/platform-profile/*; do [ -d "$d" ] && { file "$d/name"; file "$d/profile"; file "$d/choices"; }; done

# --- the Acer modules
dir /sys/module/acer_wmi
for f in /sys/module/acer_wmi/parameters/*; do file "$f"; done
attrs /sys/devices/platform/acer-wmi
dir /sys/module/linuwu_sense
for root in /sys/module/linuwu_sense/drivers/platform:acer-wmi/acer-wmi; do
    [ -d "$root" ] || continue
    emit "@@ dir $root"
    for sense in nitro_sense predator_sense; do
        [ -d "$root/$sense" ] || continue
        emit "@@ dir $root/$sense"
        for f in "$root/$sense"/*; do
            if [ -f "$f" ]; then file "$f"; elif [ -d "$f" ]; then emit "@@ dir $f"; for g in "$f"/*; do file "$g"; done; fi
        done
    done
done

# --- sensors
for d in /sys/class/hwmon/hwmon*; do
    [ -d "$d" ] || continue
    emit "@@ dir $d"
    file "$d/name"
    for f in "$d"/temp*_input "$d"/temp*_label "$d"/fan*_input "$d"/fan*_label "$d"/pwm[0-9] "$d"/pwm[0-9]_enable; do
        [ -f "$f" ] && file "$f"
    done
done

# --- battery and backlight (serial_number is never read)
for d in /sys/class/power_supply/*; do
    [ -d "$d" ] || continue
    emit "@@ dir $d"
    for f in type online status capacity technology manufacturer model_name cycle_count voltage_now current_now \
             power_now energy_now energy_full energy_full_design charge_now charge_full charge_full_design \
             time_to_empty_now charge_control_end_threshold charge_control_start_threshold charge_behaviour; do
        file "$d/$f"
    done
done
for d in /sys/class/backlight/*; do
    [ -d "$d" ] || continue
    emit "@@ dir $d"
    file "$d/brightness"
    file "$d/max_brightness"
done

# --- processor boost and power
file /sys/devices/system/cpu/cpufreq/boost
file /sys/devices/system/cpu/intel_pstate/no_turbo
file /sys/devices/system/cpu/amd_pstate/status
file /sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq
for d in /sys/class/powercap/intel-rapl:0 /sys/class/powercap/amd-rapl:0; do dir "$d"; file "$d/energy_uj"; done

# --- HID controllers (the keyboard colour on the KB5130 boards)
for d in /sys/class/hidraw/hidraw*; do
    [ -d "$d" ] || continue
    emit "@@ dir $d"
    # The uevent's HID_UNIQ is a device serial on many dongles and pads, and HID_PHYS a bus path; only the ids and the
    # name are needed to recognise a keyboard controller.
    if [ -r "$d/device/uevent" ]; then
        emit "@@ file $d/device/uevent"
        grep -E '^(DRIVER|HID_ID|HID_NAME)=' "$d/device/uevent" 2>/dev/null | sed 's/^/| /' >>"$TMP"
    fi
    hex "$d/device/report_descriptor"
    node "/dev/${d##*/}"
done

# --- the GPUs
for d in /sys/bus/pci/devices/*; do
    [ -f "$d/class" ] || continue
    case "$(cat "$d/class" 2>/dev/null)" in 0x03*) ;; *) continue ;; esac
    emit "@@ dir $d"
    file "$d/vendor"
    file "$d/device"
    file "$d/class"
    file "$d/power/runtime_status"
done

# --- input devices (names only: the Uniq= line carries Bluetooth addresses)
if [ -r /proc/bus/input/devices ]; then
    emit "@@ info input-devices"
    grep -v -E '^(U: Uniq=|P: Phys=)' /proc/bus/input/devices | sed 's/^/| /' >>"$TMP"
fi

# --- the tools the app runs, exactly as it runs them
PP=org.freedesktop.UPower.PowerProfiles
PPP=/org/freedesktop/UPower/PowerProfiles
HP=net.hadess.PowerProfiles
HPP=/net/hadess/PowerProfiles
cmd busctl --system --json=short get-property $PP $PPP $PP ActiveProfile
cmd busctl --system --json=short get-property $PP $PPP $PP Profiles
cmd busctl --system get-property $PP $PPP $PP ActiveProfile
cmd busctl --system --json=short get-property $HP $HPP $HP ActiveProfile
cmd busctl --system --json=short get-property $HP $HPP $HP Profiles
cmd busctl --system get-property $HP $HPP $HP ActiveProfile
cmd powerprofilesctl get
cmd nvidia-smi --query-gpu=name,temperature.gpu,utilization.gpu,memory.used,memory.total,clocks.gr,power.draw --format=csv,noheader,nounits
for tool in pkexec sh; do
    if command -v "$tool" >/dev/null 2>&1; then emit "@@ which $tool present"; else emit "@@ which $tool absent"; fi
done

# --- context for a human reader; not replayed
info() { label="$1"; shift; out="$(timeout 5 "$@" 2>&1)"; emit "@@ info $label"; printf '%s\n' "$out" | sed 's/^/| /' >>"$TMP"; }
info lsmod sh -c "lsmod | grep -Ei 'acer|linuwu|wmi|amd_pmf|platform_profile|nvidia|hid' || true"
info modinfo-acer-wmi sh -c "modinfo acer_wmi 2>/dev/null | grep -E '^(parm|version|filename)' || true"
info lsusb sh -c "lsusb 2>/dev/null || true"
info secure-boot sh -c "mokutil --sb-state 2>/dev/null || echo unknown"
info groups sh -c "for g in input nitrolite; do if id -nG | tr ' ' '\n' | grep -qx \"\$g\"; then echo \"\$g yes\"; else echo \"\$g no\"; fi; done"
info desktop sh -c "echo \"\${XDG_CURRENT_DESKTOP:-unknown} / \${XDG_SESSION_TYPE:-unknown}\""

emit "@@ end"

# --- the finished file names neither this machine nor this user
host="$(hostname 2>/dev/null || cat /proc/sys/kernel/hostname 2>/dev/null || echo)"
user="$(id -un 2>/dev/null || echo "${USER:-}")"
home="${HOME:-}"
escape() { printf '%s' "$1" | sed 's/[][\.*^$/]/\\&/g'; }
script=""
[ -n "$home" ] && [ "$home" != "/" ] && script="${script}s/$(escape "$home")/\/home\/<user>/g;"
[ ${#host} -ge 3 ] && script="${script}s/$(escape "$host")/<machine>/g;"
[ ${#user} -ge 3 ] && script="${script}s/$(escape "$user")/<user>/g;"
if [ -n "$script" ]; then sed "$script" "$TMP" >"$OUT"; else cp "$TMP" "$OUT"; fi

echo "Capture written to: $OUT"
echo "It contains no serial numbers, host name or user name. Send this file; nothing else is needed."
