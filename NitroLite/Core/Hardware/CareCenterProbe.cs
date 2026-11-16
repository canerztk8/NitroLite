namespace NitroLite.Core.Hardware;

/// <summary>
/// Whether the Acer Care Center agent is on this machine and running.
/// <para>
/// The 80 % charge limit is not always a WMI interface. An AN16-41 registers no <c>WMID_GUID5</c> class at all: the
/// limit is owned by <c>AcerCCAgentSvis</c>, which NitroSense and NitroLite both reach over
/// <c>wss://localhost:4343</c>. A probe that only looked for the WMI class reported a working feature as missing, so
/// the walkthrough told the user their laptop could not do the one thing the battery page was already doing.
/// </para>
/// <para>
/// The check is the service state rather than a socket connect: it is a single service-manager query, it cannot block
/// on a firewall prompt, and a running agent is the same condition the battery page needs. The live read still decides
/// what the checkbox shows, because a running agent with a rotated certificate answers nothing.
/// </para>
/// </summary>
public static class CareCenterProbe
{
    /// <summary>The Acer Care Center agent service. Display name: "Acer Care Center".</summary>
    public const string ServiceName = "AcerCCAgentSvis";

    /// <summary>Reads the service state. Never throws; a failure reads as "not available".</summary>
    public static CareCenterAvailability Read()
    {
        string? state;
        try
        {
            state = DebloatService.QueryServiceStatusDirect(ServiceName);
        }
        catch
        {
            return new CareCenterAvailability(false, $"{ServiceName} state unreadable");
        }

        return state switch
        {
            null => new CareCenterAvailability(false, $"{ServiceName} not installed"),
            "Running" => new CareCenterAvailability(true, $"Acer Care Center ({ServiceName} running)"),
            _ => new CareCenterAvailability(false, $"{ServiceName} {state.ToLowerInvariant()}")
        };
    }
}

/// <summary>What <see cref="CareCenterProbe"/> saw, and the line the hardware report prints.</summary>
public readonly record struct CareCenterAvailability(bool Available, string Detail);
