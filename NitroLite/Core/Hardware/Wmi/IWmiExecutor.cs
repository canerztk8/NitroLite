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
    /// Sensor read: <c>GetGamingSysInfo</c> plus both fan speeds, the active mode and the fan behaviour. The raw
    /// eight bytes are included so the sensor offsets in <c>profiles.json</c> can be confirmed against a real read.
    /// </summary>
    WmiOperationResult ReadTelemetry();

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
}
