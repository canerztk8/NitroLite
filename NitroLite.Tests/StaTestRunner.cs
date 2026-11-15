using System;
using System.Threading;
using System.Threading.Tasks;

namespace NitroLite.Tests;

/// <summary>
/// Runs a test body on a dedicated STA thread. WPF objects (windows, dispatchers, clipboard) refuse to be
/// created on the xUnit worker threads, which are MTA.
/// </summary>
internal static class StaTestRunner
{
    internal static async Task RunInStaAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource<object?>();
        var thread = new Thread(async () =>
        {
            try
            {
                await action();
                tcs.SetResult(null);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        await tcs.Task;
    }
}
