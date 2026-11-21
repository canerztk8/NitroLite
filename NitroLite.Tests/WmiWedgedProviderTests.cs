using System.Runtime.InteropServices;
using NitroLite.Core.Hardware.Wmi;
using Xunit;

namespace NitroLite.Tests;

#pragma warning disable CA2201 // the classifier is fed the exact exception types WMI throws

/// <summary>
/// Which WMI errors make the executor drop its cached instances and look them up again: the provider or its object
/// being gone, not a firmware that refused a parameter.
/// </summary>
public class WmiWedgedProviderTests
{
    [Theory]
    [InlineData(unchecked((int)0x80010108))] // RPC_E_DISCONNECTED
    [InlineData(unchecked((int)0x800706BA))] // RPC_S_SERVER_UNAVAILABLE
    [InlineData(unchecked((int)0x800706BE))] // RPC_S_CALL_FAILED
    public void ABrokenComConnection_IsWedged(int hresult) =>
        Assert.True(AcerWmiExecutor.IsWedged(new COMException("gone", hresult)));

    [Fact]
    public void AReleasedComObject_IsWedged() =>
        Assert.True(AcerWmiExecutor.IsWedged(new InvalidComObjectException()));

    [Theory]
    [InlineData(unchecked((int)0x80070005))] // E_ACCESSDENIED: not elevated, which looking again will not fix
    [InlineData(unchecked((int)0x80041008))] // WBEM_E_INVALID_PARAMETER as a COM error
    public void AnOrdinaryRefusal_IsNot(int hresult) =>
        Assert.False(AcerWmiExecutor.IsWedged(new COMException("no", hresult)));

    [Fact]
    public void OtherExceptions_AreNot()
    {
        Assert.False(AcerWmiExecutor.IsWedged(new InvalidOperationException()));
        Assert.False(AcerWmiExecutor.IsWedged(new ArgumentException()));
    }
}
