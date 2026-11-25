namespace NitroLite.Linux;

/// <summary>
/// Every path the Linux backend reads or writes, in one place.
/// </summary>
/// <remarks>
/// The two roots are not interchangeable. <c>/sys/devices/platform/acer-wmi</c> is what the in-kernel
/// <c>acer-wmi</c> module registers, and it is the only one that exists on a stock distribution. The
/// <c>/sys/module/linuwu_sense/...</c> tree is what the out-of-tree Linuwu-Sense module adds, and it is where the
/// fan, RGB, battery-limiter and LCD-override attributes live. The out-of-tree acer-wmi-linuwu module replaces the
/// in-kernel driver under its own name, so its <c>nitro_sense</c>/<c>predator_sense</c> and <c>four_zoned_kb</c>
/// directories sit under <c>/sys/devices/platform/acer-wmi</c>. A machine can have any of these, or none, so the probe
/// looks at all of them and the controller uses whichever answered.
/// </remarks>
public static class LinuxPaths
{
    /// <summary>SMBIOS strings, exposed by the kernel as plain files. Readable without any privilege.</summary>
    public const string DmiRoot = "/sys/class/dmi/id";

    /// <summary>The ACPI platform profile the kernel's <c>acer-wmi</c> driver registers.</summary>
    public const string PlatformProfile = "/sys/firmware/acpi/platform_profile";

    /// <summary>The profiles that file accepts, space separated.</summary>
    public const string PlatformProfileChoices = "/sys/firmware/acpi/platform_profile_choices";

    /// <summary>One directory per hwmon device, each with a <c>name</c> file.</summary>
    public const string HwmonRoot = "/sys/class/hwmon";

    /// <summary>One directory per battery and adapter.</summary>
    public const string PowerSupplyRoot = "/sys/class/power_supply";

    /// <summary>What the in-kernel <c>acer-wmi</c> module registers.</summary>
    public const string AcpiPlatformRoot = "/sys/devices/platform/acer-wmi";

    /// <summary>What the out-of-tree Linuwu-Sense module registers.</summary>
    public const string LinuwuModuleRoot = "/sys/module/linuwu_sense/drivers/platform:acer-wmi/acer-wmi";

    /// <summary>Predator machines: every feature except the custom boot logo.</summary>
    public const string PredatorSense = "predator_sense";

    /// <summary>Nitro machines: the same thermal and fan interface under a different name.</summary>
    public const string NitroSense = "nitro_sense";

    /// <summary>Four-zone keyboard RGB. Absent on single-colour and per-key keyboards.</summary>
    public const string FourZoneKeyboard = "four_zoned_kb";

    /// <summary>Fan speed as <c>cpu,gpu</c> percentages. 0 means auto.</summary>
    public const string FanSpeed = "fan_speed";

    /// <summary>0 or 1: turn the keyboard backlight off after 30 seconds idle.</summary>
    public const string BacklightTimeout = "backlight_timeout";

    /// <summary>0 or 1: stop charging at 80 %.</summary>
    public const string BatteryLimiter = "battery_limiter";

    /// <summary>0 or 1: reduce panel latency at the cost of some contrast. Linuwu-Sense's name for it.</summary>
    public const string LcdOverride = "lcd_override";

    /// <summary>The same switch under acer-wmi-linuwu's name; that module keeps <c>lcd_override</c> as an alias.</summary>
    public const string LcdOverdrive = "lcd_overdrive";

    /// <summary>0 or 1: disable the Windows and menu keys. Only acer-wmi-linuwu exposes it.</summary>
    public const string WinKeyLock = "win_key_lock";

    /// <summary>0 or 1: run both fans above the firmware curve. Not every module exposes it.</summary>
    public const string CoolBoost = "cool_boost";

    /// <summary>GPU mode. Not every module exposes it; the probe only enables MUX when it is there.</summary>
    public const string DgpuMode = "dgpu_mode";

    /// <summary>Four zone colours plus a brightness, comma separated.</summary>
    public const string PerZoneMode = "per_zone_mode";

    /// <summary>Effect, speed, brightness, direction and one colour, comma separated.</summary>
    public const string FourZoneMode = "four_zone_mode";

    /// <summary>The standard kernel battery charge limit, 0-100.</summary>
    public const string ChargeControlEndThreshold = "charge_control_end_threshold";

    /// <summary>
    /// The battery's charge type, with the active one in brackets: <c>[Standard] Long Life</c>. The in-kernel
    /// <c>acer-wmi-battery</c> driver (Linux 7.3) offers exactly these two on the models in its list, and "Long Life" is
    /// Acer's 80 % health mode — the kernel's way of exposing it, since Acer has no percentage threshold.
    /// </summary>
    public const string ChargeTypes = "charge_types";

    /// <summary>The charge type that stops at 80 %.</summary>
    public const string ChargeTypeLongLife = "Long Life";

    /// <summary>The charge type that charges to full.</summary>
    public const string ChargeTypeStandard = "Standard";

    /// <summary>
    /// The process table. Read through the same file-system abstraction as <c>/sys</c> because both are kernel
    /// pseudo-filesystems with identical semantics: a directory per entry, plain files inside, no seeking.
    /// </summary>
    public const string ProcRoot = "/proc";

    /// <summary>The kernel's short process name, truncated to 15 characters.</summary>
    public const string ProcComm = "comm";

    /// <summary>The full command line, NUL separated. The first field is the executable.</summary>
    public const string ProcCmdline = "cmdline";

    /// <summary>One directory per panel backlight device.</summary>
    public const string BacklightRoot = "/sys/class/backlight";

    /// <summary>Current backlight level, in the device's own units.</summary>
    public const string Brightness = "brightness";

    /// <summary>The level that means 100 %.</summary>
    public const string MaxBrightness = "max_brightness";

    /// <summary>0 or 1: the processor's own boost (turbo) control. Present on most AMD and many Intel machines.</summary>
    public const string CpuFreqBoost = "/sys/devices/system/cpu/cpufreq/boost";

    /// <summary>0 or 1, inverted: 1 means turbo is disabled. The Intel equivalent of <see cref="CpuFreqBoost"/>.</summary>
    public const string IntelNoTurbo = "/sys/devices/system/cpu/intel_pstate/no_turbo";

    /// <summary>
    /// <c>active</c> or <c>passive</c>. The closest thing <c>amd_pstate</c> offers to a boost switch: the passive
    /// governor does not scale above the nominal frequency, which is what school mode wants.
    /// </summary>
    public const string AmdPstateStatus = "/sys/devices/system/cpu/amd_pstate/status";

    /// <summary>
    /// The in-kernel <c>acer-wmi</c> switch that turns on the PredatorSense v4 features (platform profile, fan
    /// tachometers) on a model the driver's quirk table does not list. Present only on kernels that have the switch;
    /// <c>N</c> when it is off.
    /// </summary>
    public const string AcerWmiPredatorV4 = "/sys/module/acer_wmi/parameters/predator_v4";

    /// <summary><c>/sys/module/acer_wmi</c>: present whenever the in-kernel module is loaded.</summary>
    public const string AcerWmiModule = "/sys/module/acer_wmi";

    /// <summary>Joins a root and a relative attribute name with a single slash.</summary>
    public static string Combine(string root, string name) => $"{root.TrimEnd('/')}/{name}";
}
