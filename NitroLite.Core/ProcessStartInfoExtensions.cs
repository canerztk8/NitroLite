using System.Diagnostics;

namespace NitroLite.Core;

/// <summary>
/// Builds a <see cref="ProcessStartInfo"/> whose arguments are passed one by one.
/// </summary>
/// <remarks>
/// <see cref="ProcessStartInfo.Arguments"/> is a single string that the child process parses again, so a value
/// spliced into it can close the quote it was wrapped in and become extra arguments of its own.
/// <see cref="ProcessStartInfo.ArgumentList"/> is quoted by the runtime instead, which is the only form that stays
/// correct for a value the app did not write.
/// </remarks>
public static class ProcessStartInfoExtensions
{
    /// <summary>Adds each of <paramref name="arguments"/> to <see cref="ProcessStartInfo.ArgumentList"/>.</summary>
    public static ProcessStartInfo WithArguments(this ProcessStartInfo info, params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(info);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
}
