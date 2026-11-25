namespace NitroLite.Core;

/// <summary>The real power scheme, through powrprof.dll.</summary>
/// <remarks>
/// This is the Windows half of <see cref="IPowerSchemeAccess"/>: the contract is in NitroLite.Core so school mode
/// can be compiled and tested without a power scheme, and the implementation stays here because it is a P/Invoke.
/// </remarks>
public sealed class WindowsPowerSchemeAccess : IPowerSchemeAccess
{
    public bool TryReadDc(Guid subGroup, Guid setting, out uint value) =>
        CpuPowerService.TryReadDcValue(subGroup, setting, out value);

    public bool TryWriteDc(Guid subGroup, Guid setting, uint value) =>
        CpuPowerService.TryWriteDcValue(subGroup, setting, value);

    public Guid? ActiveScheme => CpuPowerService.TryGetActiveScheme(out var scheme) ? scheme : null;

    public bool SchemeExists(Guid scheme) => CpuPowerService.SchemeExists(scheme);

    public bool TryWriteDc(Guid scheme, Guid subGroup, Guid setting, uint value) =>
        CpuPowerService.TryWriteDcValue(scheme, subGroup, setting, value);
}
