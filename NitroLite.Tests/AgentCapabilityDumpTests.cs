using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using NitroLite.Core;
using NitroLite.Windows.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// <c>--agent-capabilities</c>: the questions NitroSense asks Acer's service at start-up, asked read-only and printed.
/// </summary>
public class AgentCapabilityDumpTests
{
    [Fact]
    public void TheQuestionList_HoldsOnlyReads()
    {
        // 100 is SET_DEVICE_DATA. A dump that could write would be a diagnostic that changes the machine it describes.
        Assert.All(AcerAgentClient.CapabilityQuestions, q => Assert.Contains(q.Packet, new uint[] { 0, 20, 30, 40 }));
        Assert.Contains(AcerAgentClient.CapabilityQuestions, q => q.Function == "SUPPORT_FAN_CONTROL");
        Assert.Equal(
            AcerAgentClient.CapabilityQuestions.Count,
            AcerAgentClient.CapabilityQuestions.Select(q => q.Function).Distinct().Count());
    }

    [Fact]
    public async Task EveryQuestionIsAsked_AndEveryAnswerPrinted()
    {
        using var service = new EchoService(function => function == "SUPPORT_FAN_CONTROL"
            ? "{\"request\":\"SUPPORT_FAN_CONTROL\",\"result\":\"0\",\"data\":[{\"fan_index\":\"0\",\"fan_custom_min_speed\":\"20\",\"fan_custom_max_speed\":\"100\"}]}"
            : function == "SUPPORT_LCD_OVERDRIVE"
                ? null
                : $"{{\"request\":\"{function}\",\"result\":\"0\",\"data\":1}}");
        using var client = new AcerAgentClient("127.0.0.1", service.Port,
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));

        using var output = new StringWriter();
        Assert.Equal(0, await AgentCapabilityDump.RunAsync(client, output));

        var text = output.ToString();
        Assert.Contains("fan_custom_min_speed", text, StringComparison.Ordinal);
        Assert.Contains("SUPPORT_LCD_OVERDRIVE: (no answer)", text, StringComparison.Ordinal);
        foreach (var (_, function) in AcerAgentClient.CapabilityQuestions)
            Assert.Contains(function + ":", text, StringComparison.Ordinal);

        Assert.DoesNotContain(100u, service.Packets);
    }

    [Fact]
    public async Task NoService_SaysSo_AndAsksNothing()
    {
        // A port nothing listens on.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var client = new AcerAgentClient("127.0.0.1", port, TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300));
        using var output = new StringWriter();

        Assert.Equal(1, await AgentCapabilityDump.RunAsync(client, output));
        Assert.Contains("did not answer", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A service that answers each request by its Function name, and records the packet ids.</summary>
    internal sealed class EchoService : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<string, string?> _answer;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public EchoService(Func<string, string?> answer)
        {
            _answer = answer;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(LoopAsync);
        }

        public int Port { get; }

        public ConcurrentQueue<uint> Packets { get; } = new();

        private async Task LoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    await ServeAsync(client).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var stream = client.GetStream();
            var pending = new List<byte>();
            var buffer = new byte[4096];
            while (!_cts.IsCancellationRequested)
            {
                int read;
                try { read = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { return; }
                if (read <= 0) return;
                pending.AddRange(buffer.Take(read));

                while (pending.Count >= 9)
                {
                    var all = pending.ToArray();
                    if (!AcerAgentClient.TryFindJsonObject(all.AsSpan(8), out _, out int end)) break;
                    uint packet = BitConverter.ToUInt32(all, 4);
                    var json = Encoding.UTF8.GetString(all, 8, end);
                    pending.RemoveRange(0, 8 + end);
                    Packets.Enqueue(packet);

                    var function = JsonNode.Parse(json)?["Function"]?.GetValue<string>() ?? "";
                    var reply = function == "VERSION"
                        ? "{\"request\":\"VERSION\",\"result\":\"0\"}"
                        : _answer(function);
                    if (reply == null) continue;

                    try
                    {
                        var bytes = Encoding.UTF8.GetBytes(reply);
                        await stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { return; }
                }
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }
}
