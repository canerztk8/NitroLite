namespace NitroLite.Core.Hardware.Wmi;

/// <summary>
/// Thrown by a write when there was no transport to write through: the elevated helper is gone, the pipe dropped, or
/// no helper was ever started.
/// <para>
/// It exists so the read-back policy can tell the two failures apart that both look like "the write returned false".
/// One is the firmware accepting a value and reporting the old one — a real capability fault, and counting it is the
/// point of <see cref="ReadBackPolicy"/>. The other is nothing having reached the hardware at all, which the policy
/// must not count: three of those in an afternoon used to switch a perfectly working fan control off for good, and on
/// the AN16-61 report they were logged as proof that the firmware refused the write, on a machine where the helper had
/// never started.
/// </para>
/// </summary>
public sealed class WmiTransportUnavailableException : Exception
{
    public WmiTransportUnavailableException(string? reason = null)
        : base(string.IsNullOrWhiteSpace(reason) ? "the elevated WMI worker is not running" : reason)
    {
    }
}
