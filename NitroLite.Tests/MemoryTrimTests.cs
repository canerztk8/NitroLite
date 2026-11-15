using System;
using System.Reflection;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The working-set trim is a one-line P/Invoke whose signature decides whether it does anything at all. On x64 the
/// two size arguments are SIZE_T, and the old <c>int</c> declaration passed 0x00000000FFFFFFFF instead of the
/// all-ones value that means "trim every page": the call returned success and trimmed nothing.
/// </summary>
public class MemoryTrimTests
{
    [Fact]
    public void SetProcessWorkingSetSize_TakesSixtyFourBitSizes()
    {
        var method = typeof(MainWindow).GetMethod(
            "SetProcessWorkingSetSize",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Equal(3, parameters.Length);

        // nint is System.IntPtr: eight bytes on x64, which is what SIZE_T is.
        Assert.Equal(typeof(IntPtr), parameters[1].ParameterType);
        Assert.Equal(typeof(IntPtr), parameters[2].ParameterType);
    }

    [Fact]
    public void TrimMemory_IsAcceptedByTheKernel()
    {
        // A valid process handle and -1/-1 always succeed; a false here means the declaration is wrong again.
        Assert.True(MainWindow.TrimMemory());
    }
}
