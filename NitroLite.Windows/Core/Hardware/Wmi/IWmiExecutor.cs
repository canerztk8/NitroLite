using System.Text.Json.Nodes;

namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// The outcome of one hardware operation, as the worker reports it back over the pipe.
/// <para>
/// <see cref="ErrorCode"/> is a stable machine-readable token; <see cref="Error"/> is the sentence a human reads.
/// Keeping them apart matters because the caller has to branch on the code — "not supported on this model" and
/// "the firmware refused" lead to different UI — while the log wants the sentence.
/// </para>
/// </summary>
public sealed record WmiOperationResult
{
    /// <summary>The call was accepted and the firmware reported success.</summary>
    public const string CodeOk = "";

    /// <summary>The machine does not expose this interface at all. Not an error: a Nitro V 15 has no MUX switch.</summary>
    public const string CodeNotSupported = "not_supported";

    /// <summary>The caller asked for something outside the allowed range. Rejected before any WMI call.</summary>
    public const string CodeInvalidArgument = "invalid_argument";

    /// <summary>The WMI call itself threw.</summary>
    public const string CodeWmiFailed = "wmi_failed";

    /// <summary>The call returned, but the firmware's status byte was not zero.</summary>
    public const string CodeStatusError = "status_error";

    /// <summary>The Acer WMI class is not registered, so nothing can be done.</summary>
    public const string CodeUnavailable = "unavailable";

    public bool Ok { get; init; }
    public string ErrorCode { get; init; } = CodeOk;
    public string Error { get; init; } = "";
    public JsonObject? Data { get; init; }

    public static WmiOperationResult Success(JsonObject? data = null) => new() { Ok = true, Data = data };

    public static WmiOperationResult Failure(string code, string error) =>
        new() { Ok = false, ErrorCode = code, Error = error };

    public static WmiOperationResult NotSupported(string error) => Failure(CodeNotSupported, error);

    public static WmiOperationResult InvalidArgument(string error) => Failure(CodeInvalidArgument, error);

    public static WmiOperationResult WmiFailed(string error) => Failure(CodeWmiFailed, error);

    public static WmiOperationResult StatusError(string error) => Failure(CodeStatusError, error);

    public static WmiOperationResult Unavailable(string error) => Failure(CodeUnavailable, error);
}

/// <summary>
/// Which firmware sensor id answers each reading. The ids come from the matched model profile, so a machine whose
/// firmware numbers its sensors differently is a data change rather than a code change.
/// <para>
/// They travel to the worker as part of the <c>ReadTelemetry</c> command and are validated there like any other
/// argument: the worker never trusts the caller, even for a read.
/// </para>
/// </summary>
public sealed record SensorQuery
{
    public byte CpuTemp { get; init; } = AcerWmiProtocol.SensorCpuTemperature;
    public byte CpuFanRpm { get; init; } = AcerWmiProtocol.SensorCpuFanSpeed;
    public byte Sys1Temp { get; init; } = AcerWmiProtocol.SensorSystemTemperature;
    public byte GpuFanRpm { get; init; } = AcerWmiProtocol.SensorGpuFanSpeed;
    public byte GpuTemp { get; init; } = AcerWmiProtocol.SensorGpuTemperature;

    /// <summary>
    /// Whether the caller has already confirmed this machine's tachometer. When it has, and both fans answer, the
    /// percentage fallback is not read: it is two firmware round trips per sample for a value the caller discards.
    /// </summary>
    public bool TachometerConfirmed { get; init; }

    /// <summary>The firmware-wide enum, used when no profile has been matched.</summary>
    public static SensorQuery Default { get; } = new();

    /// <summary>Projects a profile's layout onto the ids the worker is asked about.</summary>
    public static SensorQuery FromLayout(SensorLayout layout) => new()
    {
        CpuTemp = (byte)layout.CpuTempSensor,
        CpuFanRpm = (byte)layout.CpuFanRpmSensor,
        Sys1Temp = (byte)layout.Sys1TempSensor,
        GpuFanRpm = (byte)layout.GpuFanRpmSensor,
        GpuTemp = (byte)layout.GpuTempSensor
    };
}

/// <summary>
/// One keyboard lighting write: an effect, its parameters, and one colour per zone.
/// <para>
/// Colours are packed <c>0xRRGGBB</c> integers rather than strings so the worker validates a number instead of
/// parsing text. <see cref="Brightness"/> is the firmware's 0..100 percentage, not the app's 0..9 level: the scaling
/// of the per-zone colours happens in the executor, where the firmware's behaviour is known.
/// </para>
/// </summary>
public sealed record KeyboardLightingRequest
{
    /// <summary>App effect name: STATIC, BREATHING, NEON, WAVE, SHIFTING or ZOOM.</summary>
    public string Effect { get; init; } = "STATIC";

    /// <summary>0..100. The firmware does not apply this to a per-zone colour, so the executor scales the colour.</summary>
    public int Brightness { get; init; } = 100;

    /// <summary>1..9. Ignored by the static lock.</summary>
    public int Speed { get; init; } = 3;

    /// <summary>1 right, 2 left, 3 up, 4 down. Ignored by the static lock.</summary>
    public int Direction { get; init; } = 1;

    /// <summary>Exactly four packed <c>0xRRGGBB</c> colours, zone 1 first.</summary>
    public IReadOnlyList<int> Zones { get; init; } = new[] { 0, 0, 0, 0 };
}

/// <summary>
/// What the elevated worker can do to the hardware. Every member is a typed operation, never a generic
/// "invoke this method with this payload": the worker is the one process holding an elevated token, so the set of
/// things it can be asked to do has to be a closed list that a reviewer can read in one screen.
/// </summary>
public interface IWmiExecutor : IDisposable
{
    /// <summary>True when the Acer gaming WMI class could be reached.</summary>
    bool IsAvailable { get; }

    /// <summary>Liveness check. Answers without touching the hardware.</summary>
    WmiOperationResult Ping();

    /// <summary>Which interfaces this machine exposes: gaming class, battery class, and the capabilities behind them.</summary>
    WmiOperationResult GetCapabilities();

    /// <summary>
    /// Sensor read: one <c>GetGamingSysInfo</c> call per id in <paramref name="query"/>, plus both fan speeds, the
    /// active mode and the fan behaviour. Each sensor is reported with its own status byte so the caller can tell a
    /// real reading from a firmware that answered nothing.
    /// </summary>
    WmiOperationResult ReadTelemetry(SensorQuery query);

    /// <summary>Everything the window needs to draw its controls: mode, fan modes and speeds, MUX, battery limit.</summary>
    WmiOperationResult ReadDeviceState();

    /// <summary>Sets the CPU and GPU fan behaviour (Auto / Max / Custom).</summary>
    WmiOperationResult SetFanBehavior(FanMode cpu, FanMode gpu);

    /// <summary>Sets the CPU and GPU fan percentage. 0 releases the fan back to the firmware.</summary>
    WmiOperationResult SetFanSpeed(int cpuPercent, int gpuPercent);

    /// <summary>Sets the platform power profile.</summary>
    WmiOperationResult SetOperatingMode(OperatingMode mode);

    /// <summary>Switches the MUX. Only Optimus and discrete are offered; automatic is a firmware decision.</summary>
    WmiOperationResult SetMuxMode(GpuOperatingMode mode);

    /// <summary>Sets the battery charge limit. Reports not-supported when WMID_GUID5 is absent.</summary>
    WmiOperationResult SetBatteryLimit(BatteryChargeMode mode);

    /// <summary>
    /// Reads the keyboard backlight: brightness, the colour the firmware reports, and the raw structure from
    /// <c>GetGamingKBBacklight</c>. The raw bytes are included because the getter only ever reflects zone 1, so a
    /// caller comparing a four-zone write needs to see what the firmware actually stored.
    /// </summary>
    WmiOperationResult GetKeyboardBacklight();

    /// <summary>
    /// Writes the keyboard lighting: the four-zone sequence for a held colour, or the animated payload for an effect.
    /// <para>
    /// The sequence is <c>GetGamingSysInfo(0)</c> → <c>SetGamingLED</c> (arm all four zones) → four
    /// <c>SetGamingRgbKb</c> writes with brightness-scaled colours → <c>SetGamingKBBacklight</c> (the static lock or
    /// the effect). Skipping the arm leaves the zones disabled on the models that need it, and skipping the commit
    /// leaves the colours stored but never displayed.
    /// </para>
    /// </summary>
    WmiOperationResult SetKeyboardLighting(KeyboardLightingRequest request);

    /// <summary>
    /// Reads CoolBoost from <c>APGeAction.GetFunction(0x207)</c>. Reports not-supported when the class is absent,
    /// which is the case on every model whose firmware has no CoolBoost step.
    /// </summary>
    WmiOperationResult GetCoolBoost();

    /// <summary>Writes CoolBoost through <c>APGeAction.SetFunction</c>.</summary>
    WmiOperationResult SetCoolBoost(bool on);

    /// <summary>
    /// Reads the LCD overdrive state from <c>GetGamingProfile(0)</c>. Reports not-supported when the answer is
    /// neither of the two values a supported panel returns.
    /// </summary>
    WmiOperationResult GetLcdOverdrive();

    /// <summary>Writes LCD overdrive through <c>SetGamingProfile</c>.</summary>
    WmiOperationResult SetLcdOverdrive(bool on);

    /// <summary>
    /// Writes the Windows-key lock through <c>SetGamingProfile</c>. There is no getter for this one, so the answer
    /// says <c>verified: false</c> rather than pretending a read-back happened.
    /// </summary>
    WmiOperationResult SetWinKeyLock(bool enabled);

    /// <summary>Reads the 30-second keyboard backlight timeout from <c>APGeAction.GetFunction</c>.</summary>
    WmiOperationResult GetBacklightTimeout();

    /// <summary>Writes the 30-second keyboard backlight timeout through <c>APGeAction.SetFunction</c>.</summary>
    WmiOperationResult SetBacklightTimeout(bool enabled);
}
