using System;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Shared switch for tests that change the real machine (trim every process's working set, switch the
/// connected mouse profile, change power-plan attributes). They are skipped unless
/// NITROLITE_ENABLE_LIVE_HARDWARE_TESTS=1, so a plain "dotnet test" is safe to run on any computer.
/// </summary>
internal static class LiveSystemTests
{
    public const string EnableVariable = "NITROLITE_ENABLE_LIVE_HARDWARE_TESTS";
    public const string SkipReason = "Changes the real system; set " + EnableVariable + "=1 to run.";

    public static bool Enabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";
}

public sealed class LiveSystemFactAttribute : FactAttribute
{
    public LiveSystemFactAttribute()
    {
        if (!LiveSystemTests.Enabled) Skip = LiveSystemTests.SkipReason;
    }
}

public sealed class LiveSystemTheoryAttribute : TheoryAttribute
{
    public LiveSystemTheoryAttribute()
    {
        if (!LiveSystemTests.Enabled) Skip = LiveSystemTests.SkipReason;
    }
}
