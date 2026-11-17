using System.Globalization;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.OpenRgb;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Linux;

/// <summary>
/// The Linux hardware backend: <see cref="IHardwareController"/> over <c>/sys</c>, with no kernel module of our own.
/// </summary>
/// <remarks>
/// <para>
/// Two tiers feed it. The in-kernel <c>acer-wmi</c> module registers the ACPI platform profile and <c>hwmon</c>
/// supplies temperatures and fan tachometers, which is enough for mode switching, real fan RPM and the battery charge
/// limit on a stock distribution. The out-of-tree Linuwu-Sense module adds the attributes the kernel does not have:
/// custom fan speed, four-zone keyboard RGB, CoolBoost, LCD overdrive and the backlight timeout. Whichever answered is
/// what this class uses, and a machine with only the first tier gets a smaller feature set rather than a broken app.
/// </para>
/// <para>
/// Every write goes through <see cref="ReadBackPolicy"/>. A sysfs attribute can accept a write and ignore it — the
/// 2025 chassis with ENEK5130 keyboards do exactly that for the RGB attributes, and a firmware that is mid-resume does
/// it for the fan — so the status of the write is not evidence that anything changed. The value is read back before
/// the caller is told the write worked, and a capability that keeps failing is switched off instead of being retried
/// forever.
/// </para>
/// <para>
/// A write that fails because the user may not write a root-owned attribute is reported as
/// <see cref="PermissionHint"/> rather than as a hardware fault. That is the one failure the user can fix themselves,
/// by installing the udev rule, and telling them "fan control is broken" instead would send them looking in the wrong
/// place.
/// </para>
/// </remarks>
public sealed class LinuxSysfsHardwareController : IHardwareController, IRescannableBackend, IOpenRgbStatusProvider
{
    /// <summary>The four zone colours that mean "off".</summary>
    private static readonly string[] AllZonesOff = { "#000000", "#000000", "#000000", "#000000" };

    /// <summary>The message shown when a write was refused for lack of permission.</summary>
    public const string UdevHint =
        "Yazma izni yok. /sys düğümleri root'a ait; packaging/linux/99-nitrolite.rules dosyasını " +
        "/etc/udev/rules.d/ içine kopyalayıp `sudo udevadm control --reload && sudo udevadm trigger` " +
        "komutunu çalıştırın, sonra oturumu kapatıp açın.";

    private readonly ISysfsFileSystem _fs;
    private readonly LinuxCapabilities _caps;
    private readonly ReadBackPolicy _readBack;
    private readonly OpenRgbClient? _openRgb;
    private readonly bool _ownsOpenRgb;

    /// <summary>One gate for every write, so two callers cannot interleave a write and its read-back.</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private bool _disposed;

    /// <summary>Set once the OpenRGB fallback has been tried, so a machine without it is not probed on every call.</summary>
    private bool _openRgbProbed;

    /// <summary>What the OpenRGB probe found. Only meaningful once <see cref="_openRgbProbed"/> is set.</summary>
    private bool _openRgbAvailable;

    /// <summary>
    /// <paramref name="openRgb"/> is the fallback for a keyboard whose sysfs attributes are missing or ignored. It is
    /// optional: without it the backend simply has no lighting path on such a machine, which is the truth.
    /// </summary>
    public LinuxSysfsHardwareController(
        ISysfsFileSystem fs,
        LinuxCapabilities capabilities,
        ReadBackPolicy readBack,
        OpenRgbClient? openRgb = null,
        bool ownsOpenRgb = false)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _caps = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _readBack = readBack ?? throw new ArgumentNullException(nameof(readBack));
        _openRgb = openRgb;
        _ownsOpenRgb = ownsOpenRgb && openRgb != null;
    }

    /// <summary>
    /// Probes the machine and builds a controller for it. <paramref name="settings"/> is the same settings object the
    /// rest of the app uses, because the read-back counters live in it and a rescan has to clear them.
    /// </summary>
    public static LinuxSysfsHardwareController Create(
        AppSettings settings,
        ISysfsFileSystem? fs = null,
        ProfileCatalog? catalog = null,
        OpenRgbClient? openRgb = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var fileSystem = fs ?? RealSysfsFileSystem.Instance;
        var capabilities = new LinuxCapabilityProbe(fileSystem, catalog).Probe();
        var client = openRgb ?? new OpenRgbClient();
        return new LinuxSysfsHardwareController(
            fileSystem, capabilities, new ReadBackPolicy(settings), client, ownsOpenRgb: openRgb == null);
    }

    public string BackendName => "sysfs";

    public bool IsAvailable => !_caps.Nodes.IsEmpty;

    /// <summary>What the probe concluded, for the hardware report and the settings page.</summary>
    public LinuxCapabilities Capabilities => _caps;

    /// <summary>The read-back policy, so the settings page can reset its counters on a rescan.</summary>
    public ReadBackPolicy ReadBack => _readBack;

    /// <summary>Why the last write failed, or null. Shown next to the control that failed.</summary>
    public string? LastError { get; private set; }

    /// <summary>True once a write has been refused for lack of permission, so the UI can show the udev hint once.</summary>
    public bool PermissionHintRaised { get; private set; }

    /// <summary>The udev hint, or null when no write has been refused for lack of permission.</summary>
    public string? PermissionHint => PermissionHintRaised ? UdevHint : null;

    /// <summary>The probe result in the platform-neutral shape the shared window binds to.</summary>
    IPlatformSnapshot IRescannableBackend.Snapshot => _caps;

    /// <summary>Clears the read-back counters, so a rescan retries a capability that was switched off.</summary>
    void IRescannableBackend.ResetReadBackCounters() => _readBack.ResetAll();

    public Task<bool> EnsureConnectedAsync() => Task.FromResult(IsAvailable);

    /// <summary>
    /// The OpenRGB SDK's state, for the lighting page's status line. Null until the fallback has been tried, so a
    /// machine that never needed it shows nothing rather than a claim about a server nobody asked.
    /// </summary>
    public string? OpenRgbStatus
    {
        get
        {
            if (_openRgb == null || !_openRgbProbed) return null;
            if (_openRgbAvailable) return Loc.T("OpenRGB SDK bağlı (127.0.0.1:6742)");
            var error = _openRgb.LastError;
            return string.IsNullOrWhiteSpace(error)
                ? Loc.T("OpenRGB SDK bulunamadı; sysfs düğümleri kullanılıyor.")
                : Loc.T($"OpenRGB SDK bulunamadı: {error}");
        }
    }

    /// <summary>
    /// The probe's answer, with one correction: a keyboard whose sysfs attributes are missing but which OpenRGB can
    /// drive does have lighting. The check is a real connection attempt, cached, so it is not a guess.
    /// </summary>
    public async Task<HardwareCapabilities> GetCapabilitiesAsync()
    {
        if (_caps.Hardware.KeyboardLighting || _openRgb == null) return _caps.Hardware;

        if (!_openRgbProbed)
        {
            _openRgbProbed = true;
            _openRgbAvailable = await _openRgb.ConnectAsync().ConfigureAwait(false);
        }

        return _openRgbAvailable ? _caps.Hardware with { KeyboardLighting = true } : _caps.Hardware;
    }

    // ---------------------------------------------------------------- telemetry

    /// <summary>
    /// One sample from <c>hwmon</c>. <see cref="MonitorData.IsFanRpmMeasured"/> is true only when a real
    /// <c>fan*_input</c> answered, because that is a tachometer and the stall watchdog may judge it. Without one the
    /// speeds are derived from the fan percentage and the model's ceiling, and the watchdog stays in standby rather
    /// than comparing a measurement against a restatement of what the app asked for.
    /// </summary>
    public Task<MonitorData?> GetMonitorDataAsync()
    {
        if (_disposed) return Task.FromResult<MonitorData?>(null);

        var sample = LinuxHwmon.Sample(_caps.Nodes.Hwmon);
        var measured = sample.HasMeasuredFanRpm;
        var (cpuPercent, gpuPercent) = ReadFanPercent();

        var data = new MonitorData
        {
            CpuTemperature = sample.CpuTemperatureC ?? 0,
            GpuTemperature = sample.GpuTemperatureC ?? 0,
            SysTemperature = sample.SysTemperatureC ?? 0,
            CpuFanSpeed = measured
                ? sample.CpuFanRpm ?? 0
                : DeriveRpm(cpuPercent ?? 0, _caps.Profile.MaxCpuFanRpm),
            GpuFanSpeed = measured
                ? sample.GpuFanRpm ?? 0
                : DeriveRpm(gpuPercent ?? 0, _caps.Profile.MaxGpuFanRpm),
            IsFanRpmMeasured = measured,
            Timestamp = DateTime.Now
        };

        return Task.FromResult<MonitorData?>(data);
    }

    /// <summary>Turns a fan percentage into an approximate RPM using the model's ceiling. 0 when the ceiling is unknown.</summary>
    public static int DeriveRpm(int percent, int ceilingRpm) =>
        ceilingRpm <= 0 ? 0 : (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * ceilingRpm);

    // ---------------------------------------------------------------- operating mode

    public Task<OperatingMode?> GetOperatingModeAsync()
    {
        return Task.FromResult(LinuxPlatformProfile.Parse(ReadNode(_caps.Nodes.PlatformProfile)));
    }

    /// <summary>
    /// Writes the ACPI platform profile. The read-back compares the profile <em>string</em> rather than the mode,
    /// because the mapping is not one to one: a machine that advertises only <c>performance</c> answers
    /// <c>balanced-performance</c> for <see cref="OperatingMode.Extreme"/> and <c>performance</c> for
    /// <see cref="OperatingMode.Turbo"/>, and comparing modes would call a correct write a failure.
    /// </summary>
    public async Task<bool> SetOperatingModeAsync(OperatingMode mode)
    {
        var node = _caps.Nodes.PlatformProfile;
        if (node == null) return false;

        var target = LinuxPlatformProfile.ToProfile(mode, _caps.Hardware.SupportedOperatingModes);

        var result = await ApplyAsync(
            ReadBackPolicy.OperatingMode,
            write: () => WriteNodeAsync(node, target),
            readBack: () => Task.FromResult(ReadProfileText(node)),
            matches: observed => string.Equals(observed.Value, target, StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);

        return result.Applied;
    }

    private ProfileText? ReadProfileText(string node)
    {
        var text = _fs.ReadAllText(node);
        return string.IsNullOrWhiteSpace(text) ? null : new ProfileText(text.Trim());
    }

    // ---------------------------------------------------------------- MUX

    /// <summary>
    /// The GPU mode the module reports. The encoding is not documented upstream, so both the numeric form the module
    /// uses and the word form some builds print are accepted; anything else reads as "no answer" rather than as a
    /// guess, because a wrong guess here would be written back.
    /// </summary>
    public Task<GpuOperatingMode?> GetGpuModeAsync()
    {
        return Task.FromResult(ParseGpuMode(ReadNode(_caps.Nodes.DgpuMode)));
    }

    /// <summary>One <c>dgpu_mode</c> value as a mode, or null when it is not a form this build understands.</summary>
    public static GpuOperatingMode? ParseGpuMode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        return text.Trim().ToLowerInvariant() switch
        {
            "0" or "integrated" or "igpu" or "optimus" or "hybrid" => GpuOperatingMode.Optimus,
            "1" or "discrete" or "dgpu" or "dedicated" or "nvidia" => GpuOperatingMode.DiscreteGpu,
            _ => null
        };
    }

    /// <summary>The value to write for a mode. Automatic is a firmware decision and is never written.</summary>
    public static string? EncodeGpuMode(GpuOperatingMode mode) => mode switch
    {
        GpuOperatingMode.Optimus => "0",
        GpuOperatingMode.DiscreteGpu => "1",
        _ => null
    };

    public async Task<bool> SetGpuModeAsync(GpuOperatingMode mode)
    {
        var node = _caps.Nodes.DgpuMode;
        var value = EncodeGpuMode(mode);
        if (node == null || value == null) return false;

        var result = await ApplyAsync(
            ReadBackPolicy.MuxMode,
            write: () => WriteNodeAsync(node, value),
            readBack: GetGpuModeAsync,
            matches: observed => observed == mode).ConfigureAwait(false);

        return result.Applied;
    }

    /// <summary>
    /// The raw capability value. There is no registry on Linux, so the presence of the attribute is the whole answer:
    /// 1 when the module exposes a switch, null when it does not.
    /// </summary>
    public Task<int?> GetGpuModeCapabilityAsync() =>
        Task.FromResult(_caps.Nodes.DgpuMode == null ? (int?)null : 1);

    // ---------------------------------------------------------------- fans

    public Task<FanMode?> GetFanModeAsync()
    {
        var state = ReadFanState();
        return Task.FromResult(state?.Mode);
    }

    /// <summary>
    /// Writes the fan speed. <c>fan_speed</c> takes <c>cpu,gpu</c> percentages where 0 means "let the firmware decide",
    /// so Auto and Max are the two ends of the same attribute rather than separate commands.
    /// <para>
    /// A custom speed is clamped to <see cref="FanCurve.MinPercent"/>..<see cref="FanCurve.MaxPercent"/>. The floor is
    /// not a preference: below it the fan can stall under load, and the module would happily accept the number.
    /// </para>
    /// </summary>
    public async Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1)
    {
        var node = _caps.Nodes.FanSpeed;
        if (node == null) return false;

        var (cpu, gpu) = mode switch
        {
            FanMode.Auto => (0, 0),
            FanMode.Max => (100, 100),
            _ => (NormalisePercent(cpuPercent), NormalisePercent(gpuPercent))
        };

        var target = new FanState(mode, cpu, gpu);
        var line = $"{cpu.ToString(CultureInfo.InvariantCulture)},{gpu.ToString(CultureInfo.InvariantCulture)}";

        var result = await ApplyAsync(
            mode == FanMode.Custom ? ReadBackPolicy.FanSpeed : ReadBackPolicy.FanBehavior,
            write: () => WriteNodeAsync(node, line),
            readBack: () => Task.FromResult(ReadFanState()),
            matches: observed => observed.Mode == target.Mode &&
                                 (target.Mode != FanMode.Custom ||
                                  (observed.CpuPercent == target.CpuPercent && observed.GpuPercent == target.GpuPercent)))
            .ConfigureAwait(false);

        return result.Applied;
    }

    /// <summary>
    /// A custom speed inside the range the firmware accepts. A negative value means "the caller did not care", which
    /// becomes the application's own default rather than an error.
    /// </summary>
    public static int NormalisePercent(int percent) => percent < 0
        ? 50
        : Math.Clamp(percent, FanCurve.MinPercent, FanCurve.MaxPercent);

    /// <summary>Reads <c>fan_speed</c> and classifies it. Null when the attribute is absent or unreadable.</summary>
    private FanState? ReadFanState()
    {
        var (cpu, gpu) = ReadFanPercent();
        if (cpu == null && gpu == null) return null;

        var cpuValue = cpu ?? 0;
        var gpuValue = gpu ?? 0;

        var mode = (cpuValue, gpuValue) switch
        {
            (0, 0) => FanMode.Auto,
            ( >= 100, >= 100) => FanMode.Max,
            _ => FanMode.Custom
        };

        return new FanState(mode, cpuValue, gpuValue);
    }

    private (int? Cpu, int? Gpu) ReadFanPercent() => ParseFanPercent(ReadNode(_caps.Nodes.FanSpeed));

    /// <summary>One <c>cpu,gpu</c> line as two percentages. A missing or malformed field reads as null, not as zero.</summary>
    public static (int? Cpu, int? Gpu) ParseFanPercent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);

        var parts = text.Trim().Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return (null, null);

        return (ParsePercent(parts[0]), ParsePercent(parts[1]));
    }

    private static int? ParsePercent(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0, 100)
            : null;

    // ---------------------------------------------------------------- keyboard lighting

    /// <summary>
    /// The keyboard state the module reports: four zone colours and a brightness, plus the effect when the module has
    /// one. Brightness is converted back to the app's 0..9 level so the slider lands where the user left it.
    /// </summary>
    public Task<LightingConfig?> GetLightingConfigAsync()
    {
        var zones = LinuxKeyboardProtocol.ParsePerZone(ReadNode(_caps.Nodes.PerZoneMode));
        var effect = LinuxKeyboardProtocol.ParseFourZone(ReadNode(_caps.Nodes.FourZoneMode));

        if (zones == null && effect == null) return Task.FromResult<LightingConfig?>(null);

        var colors = zones?.Zones ?? new[] { effect!.Value.Color, effect.Value.Color, effect.Value.Color, effect.Value.Color };
        var percent = zones?.BrightnessPercent ?? effect!.Value.BrightnessPercent;
        var level = LinuxKeyboardProtocol.BrightnessPercentToLevel(percent);
        var isOff = level == 0 || colors.All(c => RgbColor.Parse(c) is { R: 0, G: 0, B: 0 });

        return Task.FromResult<LightingConfig?>(new LightingConfig
        {
            Effect = isOff ? "OFF" : effect != null ? LinuxKeyboardProtocol.EffectFor(effect.Value.Mode) : "STATIC",
            Brightness = isOff ? 0 : Math.Max(level, 1),
            Speed = Math.Clamp(effect?.Speed ?? 3, 1, LinuxKeyboardProtocol.MaxSpeed),
            Direction = Math.Clamp(effect?.Direction ?? 1, 1, LinuxKeyboardProtocol.MaxDirection),
            Color = colors[0],
            LEDs = colors.ToList()
        });
    }

    /// <summary>
    /// Writes the keyboard lighting. A static request goes to <c>per_zone_mode</c>, which is the attribute that carries
    /// four colours; an animated one goes to <c>four_zone_mode</c>, which carries the effect, its speed and its
    /// direction. When neither attribute exists the OpenRGB bridge is tried, and when that is not running either the
    /// answer is false rather than a write that changed nothing.
    /// </summary>
    public async Task<bool> SetLightingAsync(
        string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors)
    {
        var requested = (effect ?? "STATIC").Trim().ToUpperInvariant();
        var isOff = requested == "OFF" || brightness <= 0;
        var level = isOff ? 0 : Math.Clamp(brightness, 0, LinuxKeyboardProtocol.MaxBrightnessLevel);
        var wantsEffect = !isOff && !IsStaticEffect(requested);

        if (wantsEffect && _caps.Nodes.FourZoneMode != null)
        {
            return await WriteEffectAsync(requested, level, speed, direction, primaryColor).ConfigureAwait(false);
        }

        if (_caps.Nodes.PerZoneMode != null)
        {
            return await WriteZonesAsync(level, primaryColor, zoneColors, isOff).ConfigureAwait(false);
        }

        if (_caps.Nodes.FourZoneMode != null)
        {
            return await WriteEffectAsync(isOff ? "STATIC" : requested, level, speed, direction, primaryColor)
                .ConfigureAwait(false);
        }

        return await WriteOpenRgbAsync(primaryColor, zoneColors, isOff).ConfigureAwait(false);
    }

    /// <summary>True for the two effect names that mean "a fixed colour", which is what <c>per_zone_mode</c> holds.</summary>
    public static bool IsStaticEffect(string? effect) =>
        (effect ?? "").Trim().ToUpperInvariant() is "STATIC" or "DIRECT";

    private async Task<bool> WriteZonesAsync(int level, string primaryColor, string[]? zoneColors, bool isOff)
    {
        var node = _caps.Nodes.PerZoneMode!;
        var colors = isOff ? null : zoneColors;
        var line = LinuxKeyboardProtocol.BuildPerZone(colors, isOff ? "#000000" : primaryColor, level);
        var target = LinuxKeyboardProtocol.ParsePerZone(line);
        if (target == null) return false;
        var expected = target.Value;

        var result = await ApplyAsync(
            ReadBackPolicy.KeyboardLighting,
            write: () => WriteNodeAsync(node, line),
            readBack: () => Task.FromResult(LinuxKeyboardProtocol.ParsePerZone(ReadNode(node))),
            matches: observed => MatchesZones(observed, expected)).ConfigureAwait(false);

        return result.Applied;
    }

    /// <summary>
    /// True when the read-back is the state that was written. Turning the lights off is confirmed by the brightness
    /// alone: a firmware that keeps the last colour in its getter would otherwise fail the read-back for a write that
    /// worked, and the capability would be switched off for a fault that is not there.
    /// </summary>
    public static bool MatchesZones(LinuxZoneLighting observed, LinuxZoneLighting target)
    {
        if (observed.BrightnessPercent != target.BrightnessPercent) return false;
        if (target.BrightnessPercent == 0) return true;

        for (int i = 0; i < LinuxKeyboardProtocol.ZoneCount; i++)
        {
            if (!string.Equals(observed.Zones[i], target.Zones[i], StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    private async Task<bool> WriteEffectAsync(
        string effect, int level, int speed, int direction, string primaryColor)
    {
        var node = _caps.Nodes.FourZoneMode!;
        var line = LinuxKeyboardProtocol.BuildFourZone(effect, speed, level, direction, primaryColor);
        var target = LinuxKeyboardProtocol.ParseFourZone(line);
        if (target == null) return false;
        var expected = target.Value;

        var result = await ApplyAsync(
            ReadBackPolicy.KeyboardLighting,
            write: () => WriteNodeAsync(node, line),
            readBack: () => Task.FromResult(LinuxKeyboardProtocol.ParseFourZone(ReadNode(node))),
            matches: observed => MatchesEffect(observed, expected)).ConfigureAwait(false);

        return result.Applied;
    }

    /// <summary>
    /// True when the read-back is the effect that was written. The colour is compared only when the effect uses one:
    /// WAVE and NEON cycle the keyboard's own colours and ignore the one that was sent.
    /// </summary>
    public static bool MatchesEffect(LinuxEffectLighting observed, LinuxEffectLighting target)
    {
        if (observed.Mode != target.Mode) return false;
        if (observed.BrightnessPercent != target.BrightnessPercent) return false;
        if (target.BrightnessPercent == 0) return true;
        if (!LightingPolicy.EffectUsesColor(LinuxKeyboardProtocol.EffectFor(target.Mode))) return true;

        return string.Equals(observed.Color, target.Color, StringComparison.OrdinalIgnoreCase);
    }

    public Task<bool> TurnOffLightingAsync() =>
        SetLightingAsync("OFF", 0, 1, 1, "#000000", AllZonesOff);

    /// <summary>
    /// The OpenRGB bridge, for a keyboard whose sysfs attributes are missing or ignored. The four zone colours are
    /// spread across however many LEDs the device reports, so a 4-LED and a 100-LED keyboard both get the picture the
    /// user picked rather than the first four LEDs and nothing else.
    /// </summary>
    private async Task<bool> WriteOpenRgbAsync(string primaryColor, string[]? zoneColors, bool isOff)
    {
        if (_openRgb == null) return false;

        var controller = await _openRgb.GetControllerAsync(0).ConfigureAwait(false);
        if (controller == null)
        {
            LastError = _openRgb.LastError ?? "OpenRGB cihazı bulunamadı.";
            return false;
        }

        var ledCount = Math.Max(controller.LedCount, LinuxKeyboardProtocol.ZoneCount);
        var colors = new RgbColor[ledCount];
        for (int i = 0; i < ledCount; i++)
        {
            if (isOff)
            {
                colors[i] = default;
                continue;
            }

            var zone = Math.Min(i * LinuxKeyboardProtocol.ZoneCount / ledCount, LinuxKeyboardProtocol.ZoneCount - 1);
            var source = zoneColors != null && zone < zoneColors.Length ? zoneColors[zone] : primaryColor;
            colors[i] = RgbColor.Parse(source);
        }

        await _openRgb.SetCustomModeAsync(0).ConfigureAwait(false);
        var result = await _openRgb.UpdateLedsAsync(0, colors).ConfigureAwait(false);

        if (!result.Applied) LastError = result.Detail ?? "OpenRGB yazımı doğrulanamadı.";
        _openRgbProbed = true;
        _openRgbAvailable = result.Outcome != OpenRgbWriteOutcome.NotSupported;
        return result.Applied;
    }

    // ---------------------------------------------------------------- backlight timeout

    public Task<bool?> GetBacklightTimeoutAsync() => Task.FromResult(ReadFlag(_caps.Nodes.BacklightTimeout));

    public async Task<bool> SetBacklightTimeoutAsync(bool enable30s)
    {
        var node = _caps.Nodes.BacklightTimeout;
        if (node == null) return false;

        if (!await WriteNodeAsync(node, enable30s ? "1" : "0").ConfigureAwait(false)) return false;
        return ReadFlag(node) == enable30s;
    }

    // ---------------------------------------------------------------- battery charge limit

    /// <summary>
    /// The charge mode the firmware reports. The Linuwu-Sense limiter is a 0/1 switch for 80 %, while the standard
    /// kernel attribute is a percentage, so both are read into the same two modes.
    /// </summary>
    public Task<BatteryChargeMode?> GetBatteryChargeModeAsync()
    {
        if (_caps.Nodes.BatteryLimiter != null)
        {
            var flag = ReadFlag(_caps.Nodes.BatteryLimiter);
            return Task.FromResult(flag == null ? (BatteryChargeMode?)null : flag.Value ? BatteryChargeMode.Optimized80 : BatteryChargeMode.Full);
        }

        if (_caps.Nodes.ChargeThreshold != null)
        {
            var percent = ReadInt(_caps.Nodes.ChargeThreshold);
            return Task.FromResult(percent switch
            {
                null => (BatteryChargeMode?)null,
                <= 80 => BatteryChargeMode.Optimized80,
                _ => BatteryChargeMode.Full
            });
        }

        return Task.FromResult<BatteryChargeMode?>(null);
    }

    /// <summary>
    /// Writes the charge limit. <see cref="BatteryChargeMode.Adaptive"/> has no sysfs representation — the kernel
    /// exposes a threshold, not a policy — so it is refused rather than silently written as something else.
    /// </summary>
    public async Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode)
    {
        if (mode == BatteryChargeMode.Adaptive) return false;

        var limiter = _caps.Nodes.BatteryLimiter;
        var threshold = _caps.Nodes.ChargeThreshold;
        if (limiter == null && threshold == null) return false;

        var result = await ApplyAsync(
            ReadBackPolicy.BatteryLimit,
            write: async () =>
            {
                if (limiter != null) return await WriteNodeAsync(limiter, mode == BatteryChargeMode.Optimized80 ? "1" : "0").ConfigureAwait(false);
                return await WriteNodeAsync(threshold!, mode == BatteryChargeMode.Optimized80 ? "80" : "100").ConfigureAwait(false);
            },
            readBack: GetBatteryChargeModeAsync,
            matches: observed => observed == mode).ConfigureAwait(false);

        return result.Applied;
    }

    // ---------------------------------------------------------------- CoolBoost, overdrive, WinKey lock

    public Task<bool?> GetCoolBoostAsync() => Task.FromResult(ReadFlag(_caps.Nodes.CoolBoost));

    public async Task<bool> SetCoolBoostAsync(bool on)
    {
        var node = _caps.Nodes.CoolBoost;
        if (node == null) return false;

        if (!await WriteNodeAsync(node, on ? "1" : "0").ConfigureAwait(false)) return false;
        return ReadFlag(node) == on;
    }

    public Task<bool?> GetLcdOverdriveAsync() => Task.FromResult(ReadFlag(_caps.Nodes.LcdOverride));

    public async Task<bool> SetLcdOverdriveAsync(bool on)
    {
        var node = _caps.Nodes.LcdOverride;
        if (node == null) return false;

        if (!await WriteNodeAsync(node, on ? "1" : "0").ConfigureAwait(false)) return false;
        return ReadFlag(node) == on;
    }

    /// <summary>
    /// No Acer Linux module exposes the Windows-key lock, so there is nothing to write. False rather than a silent
    /// success: the UI hides the switch when the probe reports no support, and a caller that asks anyway is told the
    /// truth.
    /// </summary>
    public Task<bool> SetWinKeyLockAsync(bool enabled) => Task.FromResult(false);

    // ---------------------------------------------------------------- plumbing

    /// <summary>Runs one write and its read-back behind the write gate.</summary>
    private async Task<ReadBackResult> ApplyAsync<T>(
        string capability,
        Func<Task<bool>> write,
        Func<Task<T?>> readBack,
        Func<T, bool> matches) where T : struct
    {
        if (_disposed) return ReadBackResult.AlreadyDisabled();

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await _readBack.ApplyAsync(capability, write, readBack, matches).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Writes one sysfs attribute. The value is newline-terminated because that is what <c>echo</c> sends and what the
    /// module's parsers expect; a permission failure is recorded as the udev hint rather than as a hardware fault.
    /// </summary>
    private Task<bool> WriteNodeAsync(string path, string value)
    {
        var result = _fs.WriteAllText(path, value + "\n");
        if (result.Ok)
        {
            LastError = null;
            return Task.FromResult(true);
        }

        if (result.NeedsUdevRule)
        {
            PermissionHintRaised = true;
            LastError = UdevHint;
            AppLog.Warn(Loc.T("sysfs yazımı izin nedeniyle reddedildi"), $"{path}: udev kuralı gerekiyor");
            return Task.FromResult(false);
        }

        LastError = result.Status == SysfsWriteStatus.Missing
            ? $"{path} artık yok: çekirdek modülü kaldırılmış olabilir."
            : $"{path} yazılamadı: {result.Detail}";

        AppLog.Warn(Loc.T("sysfs yazımı başarısız"), $"{path}: {result.Status} {result.Detail}");
        return Task.FromResult(false);
    }

    /// <summary>A 0/1 attribute as a bool, or null when it is absent or holds something else.</summary>
    private bool? ReadFlag(string? path)
    {
        var value = ReadInt(path);
        return value switch
        {
            0 => false,
            1 => true,
            _ => null
        };
    }

    /// <summary>One attribute's contents, or null when the node is absent or unreadable.</summary>
    private string? ReadNode(string? path) => path == null ? null : _fs.ReadAllText(path);

    private int? ReadInt(string? path)
    {
        if (path == null) return null;
        var text = _fs.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _writeGate.Dispose();
        if (_ownsOpenRgb) _openRgb?.Dispose();
    }

    /// <summary>An ACPI profile string, wrapped so it can be a read-back value.</summary>
    private readonly record struct ProfileText(string Value);

    /// <summary>One fan reading: the mode it implies and the two percentages behind it.</summary>
    private readonly record struct FanState(FanMode Mode, int CpuPercent, int GpuPercent);
}
