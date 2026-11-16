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

    /// <summary>
    /// <paramref name="catalog"/> is injectable so a test can pin a specific model table; the default loads the
    /// embedded one plus <c>profiles.user.json</c> from the data folder.
    /// </summary>
    public LinuxCapabilityProbe(ISysfsFileSystem fs, ProfileCatalog? catalog = null)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _catalog = catalog ?? ProfileCatalog.Load();
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

        var hardware = new HardwareCapabilities
        {
            Backend = "sysfs",
            // "A working path to the hardware" on Linux means at least one node answered.
            ServiceAvailable = !nodes.IsEmpty,
            OperatingModeControl = hasAcpi,
            FanControl = nodes.FanSpeed != null,
            MuxSwitch = nodes.DgpuMode != null,
            BatteryChargeLimit = nodes.BatteryLimiter != null || nodes.ChargeThreshold != null,
            KeyboardLighting = nodes.PerZoneMode != null || nodes.FourZoneMode != null,
            BacklightTimeout = nodes.BacklightTimeout != null,
            CoolBoost = nodes.CoolBoost != null,
            LcdOverdrive = nodes.LcdOverride != null,
            // There is no sysfs attribute for the Windows-key lock on any Acer Linux module.
            WinKeyLock = false,
            SupportedOperatingModes = supportedModes,
            SupportedModeMask = null
        };

        if (!hasAcpi) notes.Add("platform_profile yok: çekirdek acer-wmi modülü yüklü değil, çalışma modu değiştirilemez.");
        if (!hasSense) notes.Add("Linuwu-Sense yok: özel fan hızı, klavye RGB ve CoolBoost kullanılamaz.");
        if (hwmon.Count == 0) notes.Add("hwmon cihazı bulunamadı: sıcaklık ve fan devri okunamıyor.");
        if (sample.CpuFanRpm == null && sample.GpuFanRpm == null)
        {
            notes.Add("Fan takometresi yok: fan durma koruması türetilmiş devirle çalışmaz.");
        }

        if (nodes.PerZoneMode == null && nodes.FourZoneMode == null && profile.FourZoneKeyboard == true)
        {
            notes.Add("Bu model dört bölgeli klavye taşıyor ama four_zoned_kb düğümü yok; OpenRGB köprüsü denenecek.");
        }

        // A node that exists but cannot be opened for writing is the udev case, and it is worth saying so before the
        // user presses a control and watches it fail. Checked here rather than on the first failed write so the
        // settings page can show the rule on launch.
        var needsUdevRule = NeedsUdevRule(nodes);
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
    /// True when at least one attribute the backend would write exists but this process may not open it for writing.
    /// <para>
    /// Only the nodes that carry a write are checked: a read-only attribute that cannot be written is not a
    /// permission problem, and telling the user to install a udev rule for it would be wrong.
    /// </para>
    /// </summary>
    private bool NeedsUdevRule(LinuxNodes nodes)
    {
        foreach (var path in WritableNodes(nodes))
        {
            if (!_fs.CanWrite(path)) return true;
        }

        return false;
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
                     nodes.DgpuMode
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

        // four_zoned_kb is a directory, not an attribute: the two files inside it are the ones that carry the state.
        var keyboard = sense == null ? null : ExistingDirectory(LinuxPaths.Combine(sense, LinuxPaths.FourZoneKeyboard));

        return new LinuxNodes
        {
            PlatformProfile = Existing(LinuxPaths.PlatformProfile),
            PlatformProfileChoices = Existing(LinuxPaths.PlatformProfileChoices),
            SenseRoot = sense,
            FanSpeed = Sense(LinuxPaths.FanSpeed) ?? Acpi(LinuxPaths.FanSpeed),
            BacklightTimeout = Sense(LinuxPaths.BacklightTimeout) ?? Acpi(LinuxPaths.BacklightTimeout),
            BatteryLimiter = Sense(LinuxPaths.BatteryLimiter) ?? Acpi(LinuxPaths.BatteryLimiter),
            ChargeThreshold = ResolveChargeThreshold(),
            LcdOverride = Sense(LinuxPaths.LcdOverride) ?? Acpi(LinuxPaths.LcdOverride),
            CoolBoost = Sense(LinuxPaths.CoolBoost) ?? Acpi(LinuxPaths.CoolBoost),
            DgpuMode = Sense(LinuxPaths.DgpuMode) ?? Acpi(LinuxPaths.DgpuMode),
            PerZoneMode = keyboard == null ? null : Existing(LinuxPaths.Combine(keyboard, LinuxPaths.PerZoneMode)),
            FourZoneMode = keyboard == null ? null : Existing(LinuxPaths.Combine(keyboard, LinuxPaths.FourZoneMode)),
            Hwmon = LinuxHwmon.Read(_fs)
        };
    }

    /// <summary>
    /// The module registers <c>predator_sense</c> on Predators and <c>nitro_sense</c> on Nitros, and a machine can
    /// have both directories. Predator wins because it is the superset.
    /// </summary>
    private string? ResolveSenseRoot()
    {
        foreach (var name in new[] { LinuxPaths.PredatorSense, LinuxPaths.NitroSense })
        {
            var path = LinuxPaths.Combine(LinuxPaths.LinuwuModuleRoot, name);
            if (_fs.DirectoryExists(path)) return path;
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
