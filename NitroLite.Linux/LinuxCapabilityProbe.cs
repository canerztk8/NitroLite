using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// Works out what this Linux machine can do, from the two tiers the kernel offers.
/// </summary>
/// <remarks>
/// <para>
/// Tier 1 is what a stock distribution gives you: the in-kernel <c>acer-wmi</c> module, which registers the ACPI
/// platform profile, and <c>hwmon</c>, which gives temperatures and fan tachometers. That is enough for mode
/// switching, real fan RPM and the battery charge limit, and it needs no out-of-tree module at all.
/// </para>
/// <para>
/// Tier 2 is the out-of-tree Linuwu-Sense module, which adds the attributes the kernel does not have: custom fan
/// speed, four-zone keyboard RGB, CoolBoost, LCD overdrive, the backlight timeout and the battery limiter. It is
/// optional. A machine with only tier 1 gets a working, smaller feature set rather than a broken app.
/// </para>
/// <para>
/// Nothing here is guessed from the model name. A capability is on only when the attribute that implements it
/// exists, because the same chassis ships with and without the module and a model table cannot know which.
/// </para>
/// </remarks>
public sealed class LinuxCapabilityProbe
{
    private readonly ISysfsFileSystem _fs;
    private readonly ProfileCatalog _catalog;
    private readonly IPowerProfiles _power;
    private readonly Func<string, byte[]?>? _readBytes;

    /// <summary>
    /// <paramref name="catalog"/> is injectable so a test can pin a specific model table; the default loads the
    /// embedded one plus <c>profiles.user.json</c> from the data folder. <paramref name="powerProfiles"/> defaults to
    /// "no daemon" so a test never reaches the real system bus; the app passes the real client.
    /// <paramref name="readBytes"/> reads a HID report descriptor, and defaults to the real file.
    /// </summary>
    public LinuxCapabilityProbe(
        ISysfsFileSystem fs,
        ProfileCatalog? catalog = null,
        IPowerProfiles? powerProfiles = null,
        Func<string, byte[]?>? readBytes = null)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _catalog = catalog ?? ProfileCatalog.Load();
        _power = powerProfiles ?? NoPowerProfiles.Instance;
        _readBytes = readBytes;
    }

    /// <summary>Reads everything and decides. Never throws: a missing tree is a machine with no Acer support.</summary>
    public LinuxCapabilities Probe()
    {
        var identity = LinuxDmiProbe.Read(_fs);
        var profile = _catalog.Match(identity);
        var nodes = ResolveNodes();
        var notes = new List<string>();

        var choices = _fs.ReadAllText(LinuxPaths.PlatformProfileChoices);
        var supportedModes = LinuxPlatformProfile.ParseChoices(choices);

        var hwmon = LinuxHwmon.Read(_fs);
        var sample = LinuxHwmon.Sample(hwmon);

        var hasSense = nodes.SenseRoot != null;
        var hasAcpi = nodes.PlatformProfile != null;

        // The mode goes straight to platform_profile when this user may write it (the udev rule is installed), and
        // through the desktop's power-profile daemon otherwise: the daemon writes the same attribute as root and polkit
        // lets the active session ask, so mode switching works on a stock install with nothing added.
        var profileWritable = hasAcpi && _fs.CanWrite(nodes.PlatformProfile!);
        var viaDaemon = !profileWritable && _power.IsAvailable;
        if (viaDaemon) supportedModes = PowerProfilesDaemon.ToModes(_power.Profiles);

        var hasKeyboardNodes = nodes.PerZoneMode != null || nodes.FourZoneMode != null;

        var hardware = new HardwareCapabilities
        {
            Backend = "sysfs",
            // "A working path to the hardware" on Linux means at least one node answered.
            ServiceAvailable = !nodes.IsEmpty || viaDaemon,
            OperatingModeControl = hasAcpi || viaDaemon,
            FanControl = nodes.FanSpeed != null || nodes.HasKernelFanControl,
            MuxSwitch = nodes.DgpuMode != null,
            BatteryChargeLimit = nodes.BatteryLimiter != null || nodes.ChargeTypes != null || nodes.ChargeThreshold != null,
            KeyboardLighting = hasKeyboardNodes || nodes.HidrawKeyboard != null,
            BacklightTimeout = nodes.BacklightTimeout != null,
            CoolBoost = nodes.CoolBoost != null,
            LcdOverdrive = nodes.LcdOverride != null,
            // Only acer-wmi-linuwu has the Windows-key lock; neither the kernel driver nor Linuwu-Sense does.
            WinKeyLock = nodes.WinKeyLock != null,
            SupportedOperatingModes = supportedModes,
            SupportedModeMask = null
        };

        if (!hasAcpi && !viaDaemon)
        {
            notes.Add("platform_profile yok: çekirdek acer-wmi modülü yüklü değil, çalışma modu değiştirilemez.");
        }

        if (viaDaemon) notes.Add("Çalışma modu power-profiles-daemon üzerinden değiştiriliyor.");
        if (!hasSense && !hardware.FanControl && !hardware.KeyboardLighting)
            notes.Add("Ek Acer modülü (acer-wmi-linuwu veya Linuwu-Sense) yok: özel fan hızı, klavye RGB ve CoolBoost kullanılamaz.");
        if (hwmon.Count == 0) notes.Add("hwmon cihazı bulunamadı: sıcaklık ve fan devri okunamıyor.");
        if (sample.CpuFanRpm == null && sample.GpuFanRpm == null)
        {
            notes.Add("Fan takometresi yok: fan durma koruması türetilmiş devirle çalışmaz.");
        }

        if (!hasKeyboardNodes && nodes.HidrawKeyboard != null)
        {
            notes.Add($"Klavye ışığı ENE KB5130 denetleyicisi üzerinden yazılıyor ({nodes.HidrawKeyboard}).");
        }
        else if (!hasKeyboardNodes && profile.FourZoneKeyboard == true)
        {
            notes.Add("Bu model dört bölgeli klavye taşıyor ama four_zoned_kb düğümü yok; OpenRGB köprüsü denenecek.");
        }

        var kernelHint = KernelHintFor(identity, hasSense, sample);
        var kernelVersion = LinuxModelSupport.ParseKernel(_fs.ReadAllText("/proc/sys/kernel/osrelease"));
        var kernelModel = LinuxModelSupport.Find(identity);
        if (kernelHint == KernelSetupHint.EnablePredatorV4)
        {
            notes.Add("acer-wmi predator_v4 kapalı: açılırsa fan devri ve Acer çalışma modları gelebilir.");
        }

        // A node that exists but cannot be opened for writing is the udev case, and it is worth saying so before the
        // user presses a control and watches it fail. Checked here rather than on the first failed write so the
        // settings page can show the rule on launch. platform_profile does not count when the daemon carries the mode.
        var denied = WritableNodes(nodes)
            .Where(path => !(viaDaemon && path == nodes.PlatformProfile) && !_fs.CanWrite(path))
            .ToArray();
        var needsUdevRule = denied.Length > 0;
        if (needsUdevRule)
        {
            notes.Add("sysfs düğümleri yazılabilir değil: packaging/linux/99-nitrolite.rules kurulmalı.");
        }

        return new LinuxCapabilities
        {
            Identity = identity,
            Profile = profile,
            Hardware = hardware,
            Nodes = nodes,
            NeedsUdevRule = needsUdevRule,
            DirectWriteDenied = denied,
            ModeViaPowerProfiles = viaDaemon,
            PowerProfiles = viaDaemon ? _power : NoPowerProfiles.Instance,
            KernelHint = kernelHint,
            KernelVersion = kernelVersion,
            KernelModel = kernelModel,
            ModelNote = LinuxModelSupport.Describe(kernelModel, kernelVersion),
            AcerWmiLoaded = _fs.DirectoryExists(LinuxPaths.AcerWmiModule),
            PredatorV4 = _fs.ReadAllText(LinuxPaths.AcerWmiPredatorV4)?.Trim(),
            PlatformProfileChoicesText = choices?.Trim(),
            Tier = (hasAcpi, hasSense) switch
            {
                (true, true) => "both",
                (true, false) => "acer-wmi",
                (false, true) => "linuwu-sense",
                _ => "none"
            },
            Notes = notes
        };
    }

    /// <summary>
    /// The one kernel-side step worth suggesting. Only for an Acer machine without Linuwu-Sense: with the module loaded
    /// there is nothing more the kernel can add.
    /// <para>
    /// <c>predator_v4</c> is suggested only when the loaded <c>acer-wmi</c> actually has the switch, it is off, and the
    /// fans have no tachometer — the state of a model the driver's quirk table does not list. Its presence is read from
    /// <c>/sys/module/acer_wmi/parameters</c>, so a kernel without it never gets a command that would fail.
    /// </para>
    /// </summary>
    private KernelSetupHint KernelHintFor(DeviceIdentity identity, bool hasSense, LinuxSensorSample sample)
    {
        if (!identity.IsAcer || hasSense) return KernelSetupHint.None;

        var predatorV4 = _fs.ReadAllText(LinuxPaths.AcerWmiPredatorV4)?.Trim();
        var switchOff = predatorV4 is "N" or "n" or "0";
        var noTachometer = sample.CpuFanRpm == null && sample.GpuFanRpm == null;

        if (switchOff && noTachometer) return KernelSetupHint.EnablePredatorV4;
        return KernelSetupHint.InstallLinuwuSense;
    }

    /// <summary>Every attribute the backend writes, in the order the settings page lists them.</summary>
    private static IEnumerable<string> WritableNodes(LinuxNodes nodes)
    {
        foreach (var path in new[]
                 {
                     nodes.PlatformProfile,
                     nodes.FanSpeed,
                     nodes.PerZoneMode,
                     nodes.FourZoneMode,
                     nodes.CoolBoost,
                     nodes.LcdOverride,
                     nodes.BacklightTimeout,
                     nodes.BatteryLimiter,
                     nodes.ChargeThreshold,
                     nodes.ChargeTypes,
                     nodes.WinKeyLock,
                     nodes.DgpuMode,
                     nodes.HidrawKeyboard,
                     nodes.AcerPwmCpu,
                     nodes.AcerPwmGpu,
                     nodes.AcerPwmCpuEnable,
                     nodes.AcerPwmGpuEnable
                 })
        {
            if (path != null) yield return path;
        }
    }

    /// <summary>
    /// Resolves every attribute path. The Linuwu-Sense tree is checked first because it is the richer one, then the
    /// in-kernel <c>acer-wmi</c> tree, then the standard kernel classes.
    /// </summary>
    private LinuxNodes ResolveNodes()
    {
        var sense = ResolveSenseRoot();
        var acpi = _fs.DirectoryExists(LinuxPaths.AcpiPlatformRoot) ? LinuxPaths.AcpiPlatformRoot : null;

        string? Sense(string name) => sense == null ? null : Existing(LinuxPaths.Combine(sense, name));
        string? Acpi(string name) => acpi == null ? null : Existing(LinuxPaths.Combine(acpi, name));

        // The kernel's own fan control lives on the acer hwmon device, next to the tachometers.
        var hwmon = LinuxHwmon.Read(_fs);
        string? acerHwmon = null;
        foreach (var device in hwmon)
        {
            if (device.Name.Equals("acer", StringComparison.OrdinalIgnoreCase)) { acerHwmon = device.Path; break; }
        }

        string? Pwm(string name) => acerHwmon == null ? null : Existing(LinuxPaths.Combine(acerHwmon, name));

        // four_zoned_kb is a directory, not an attribute: the two files inside it are the ones that carry the state.
        // Both modules put it next to the sense directory, on the platform device; below it is checked too, for the
        // layouts older module builds used.
        var keyboard = ResolveKeyboardDirectory(sense);

        return new LinuxNodes
        {
            PlatformProfile = Existing(LinuxPaths.PlatformProfile),
            PlatformProfileChoices = Existing(LinuxPaths.PlatformProfileChoices),
            SenseRoot = sense,
            FanSpeed = Sense(LinuxPaths.FanSpeed) ?? Acpi(LinuxPaths.FanSpeed),
            BacklightTimeout = Sense(LinuxPaths.BacklightTimeout) ?? Acpi(LinuxPaths.BacklightTimeout),
            BatteryLimiter = Sense(LinuxPaths.BatteryLimiter) ?? Acpi(LinuxPaths.BatteryLimiter),
            ChargeThreshold = ResolveChargeThreshold(),
            ChargeTypes = ResolveChargeTypes(),
            WinKeyLock = Sense(LinuxPaths.WinKeyLock) ?? Acpi(LinuxPaths.WinKeyLock),
            LcdOverride = Sense(LinuxPaths.LcdOverdrive) ?? Sense(LinuxPaths.LcdOverride) ??
                          Acpi(LinuxPaths.LcdOverdrive) ?? Acpi(LinuxPaths.LcdOverride),
            CoolBoost = Sense(LinuxPaths.CoolBoost) ?? Acpi(LinuxPaths.CoolBoost),
            DgpuMode = Sense(LinuxPaths.DgpuMode) ?? Acpi(LinuxPaths.DgpuMode),
            PerZoneMode = keyboard == null ? null : Existing(LinuxPaths.Combine(keyboard, LinuxPaths.PerZoneMode)),
            FourZoneMode = keyboard == null ? null : Existing(LinuxPaths.Combine(keyboard, LinuxPaths.FourZoneMode)),
            HidrawKeyboard = LinuxHidrawLocator.FindEneKb5130(_fs, _readBytes),
            AcerPwmCpu = Pwm("pwm1"),
            AcerPwmGpu = Pwm("pwm2"),
            AcerPwmCpuEnable = Pwm("pwm1_enable"),
            AcerPwmGpuEnable = Pwm("pwm2_enable"),
            Hwmon = hwmon
        };
    }

    /// <summary>
    /// The module registers <c>predator_sense</c> on Predators and <c>nitro_sense</c> on Nitros, and a machine can
    /// have both directories. Predator wins because it is the superset. Linuwu-Sense registers them under its own
    /// module name; acer-wmi-linuwu replaces the kernel driver, so its directories are on the acer-wmi platform device.
    /// </summary>
    private string? ResolveSenseRoot()
    {
        foreach (var parent in SenseParents)
        {
            foreach (var name in new[] { LinuxPaths.PredatorSense, LinuxPaths.NitroSense })
            {
                var path = LinuxPaths.Combine(parent, name);
                if (_fs.DirectoryExists(path)) return path;
            }
        }

        return null;
    }

    private static readonly string[] SenseParents = { LinuxPaths.LinuwuModuleRoot, LinuxPaths.AcpiPlatformRoot };

    /// <summary>The <c>four_zoned_kb</c> directory: beside the sense directory, or below it.</summary>
    private string? ResolveKeyboardDirectory(string? sense)
    {
        if (sense == null) return null;

        var parent = sense[..sense.LastIndexOf('/')];
        return ExistingDirectory(LinuxPaths.Combine(parent, LinuxPaths.FourZoneKeyboard)) ??
               ExistingDirectory(LinuxPaths.Combine(sense, LinuxPaths.FourZoneKeyboard));
    }

    /// <summary>
    /// The first battery whose <c>charge_types</c> offers both "Standard" and "Long Life", or null. Other drivers use
    /// the same attribute for other types (Fast, Adaptive, Custom), and those are not Acer's 80 % mode.
    /// </summary>
    private string? ResolveChargeTypes()
    {
        foreach (var directory in _fs.EnumerateDirectories(LinuxPaths.PowerSupplyRoot))
        {
            var name = Path.GetFileName(directory);
            if (!name.StartsWith("BAT", StringComparison.OrdinalIgnoreCase)) continue;

            var path = LinuxPaths.Combine(directory, LinuxPaths.ChargeTypes);
            var offered = LinuxChargeTypes.Parse(_fs.ReadAllText(path));
            if (offered != null && offered.Value.Offered.Contains(LinuxPaths.ChargeTypeLongLife) &&
                offered.Value.Offered.Contains(LinuxPaths.ChargeTypeStandard))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>The first battery that exposes the standard charge limit, or null.</summary>
    private string? ResolveChargeThreshold()
    {
        foreach (var directory in _fs.EnumerateDirectories(LinuxPaths.PowerSupplyRoot))
        {
            var name = Path.GetFileName(directory);
            if (!name.StartsWith("BAT", StringComparison.OrdinalIgnoreCase)) continue;

            var path = LinuxPaths.Combine(directory, LinuxPaths.ChargeControlEndThreshold);
            if (_fs.FileExists(path)) return path;
        }

        return null;
    }

    private string? Existing(string path) => _fs.FileExists(path) ? path : null;

    private string? ExistingDirectory(string path) => _fs.DirectoryExists(path) ? path : null;
}
