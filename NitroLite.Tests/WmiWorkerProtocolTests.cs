using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using NitroLite.Core.Hardware.Wmi;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The worker protocol's byte path: the same wire format as before, without the string and the second array.
/// </summary>
/// <remarks>
/// <para>
/// The framing is the one thing both processes have to agree on, so the first tests here are compatibility tests:
/// what <see cref="WmiWorkerProtocol.WriteAsync(Stream, WmiWorkerRequest, CancellationToken)"/> puts on the pipe has
/// to be byte-for-byte what the string path produced, or a window built from one commit cannot talk to a worker
/// built from another.
/// </para>
/// <para>
/// The rest is the reason the byte path exists. A message used to cost a JSON string, a payload array and a second
/// array to copy both into; it now costs one pooled buffer and one write. The allocation tests measure that and the
/// budgets are set from the measured numbers with headroom, so a return to the old path fails the build rather than
/// quietly costing a few kilobytes every three seconds.
/// </para>
/// </remarks>
public class WmiWorkerProtocolTests
{
    private static readonly WmiWorkerCommand[] AllCommands = Enum.GetValues<WmiWorkerCommand>();

    private static WmiWorkerRequest SampleRequest(WmiWorkerCommand command = WmiWorkerCommand.ReadTelemetry) => new()
    {
        Type = WmiWorkerProtocol.TypeCommand,
        Command = command,
        Args = new JsonObject { ["cpu"] = 55, ["gpu"] = 61.5, ["zones"] = new JsonArray(1, 2, 3, 4) }
    };

    private static WmiWorkerResponse SampleResponse() => new()
    {
        Type = WmiWorkerProtocol.TypeResult,
        Ok = true,
        Data = new JsonObject { ["fan"] = 2369, ["mode"] = "turbo", ["mux"] = false }
    };

    private static async Task<byte[]> WrittenAsync(Func<Stream, Task> write)
    {
        using var stream = new MemoryStream();
        await write(stream);
        return stream.ToArray();
    }

    private static MemoryStream Framed(string json)
    {
        var frame = WmiWorkerProtocol.Frame(json);
        return new MemoryStream(frame, writable: false);
    }

    [Fact]
    public async Task WritingARequestProducesTheSameBytesAsTheStringPath()
    {
        var request = SampleRequest();

        var expected = WmiWorkerProtocol.Frame(WmiWorkerProtocol.Serialize(request));
        var actual = await WrittenAsync(s => WmiWorkerProtocol.WriteAsync(s, request));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task WritingAResponseProducesTheSameBytesAsTheStringPath()
    {
        var response = SampleResponse();

        var expected = WmiWorkerProtocol.Frame(WmiWorkerProtocol.Serialize(response));
        var actual = await WrittenAsync(s => WmiWorkerProtocol.WriteAsync(s, response));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task WritingAnEmptyArgsRequestOmitsTheFieldLikeTheStringPath()
    {
        var request = new WmiWorkerRequest { Type = WmiWorkerProtocol.TypeHello, Nonce = "abc" };

        var expected = WmiWorkerProtocol.Frame(WmiWorkerProtocol.Serialize(request));
        var actual = await WrittenAsync(s => WmiWorkerProtocol.WriteAsync(s, request));

        Assert.Equal(expected, actual);
        Assert.DoesNotContain("args", Encoding.UTF8.GetString(actual), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryCommandRoundTripsThroughTheBytePath()
    {
        foreach (var command in AllCommands)
        {
            var request = SampleRequest(command);
            var bytes = await WrittenAsync(s => WmiWorkerProtocol.WriteAsync(s, request));

            using var stream = new MemoryStream(bytes, writable: false);
            var read = await WmiWorkerProtocol.ReadRequestAsync(stream);

            Assert.False(read.Closed);
            Assert.NotNull(read.Request);
            Assert.Equal(WmiWorkerProtocol.TypeCommand, read.Request!.Type);
            Assert.Equal(command, read.Request.Command);
            Assert.Equal(55, read.Request.Args!["cpu"]!.GetValue<int>());
            Assert.Equal(61.5, read.Request.Args["gpu"]!.GetValue<double>());
            Assert.Equal(4, read.Request.Args["zones"]!.AsArray().Count);
        }
    }

    [Fact]
    public async Task AResponseRoundTripsThroughTheBytePath()
    {
        var bytes = await WrittenAsync(s => WmiWorkerProtocol.WriteAsync(s, SampleResponse()));

        using var stream = new MemoryStream(bytes, writable: false);
        var response = await WmiWorkerProtocol.ReadResponseAsync(stream);

        Assert.NotNull(response);
        Assert.True(response!.Ok);
        Assert.Equal(WmiWorkerProtocol.TypeResult, response.Type);
        Assert.Equal(2369, response.Data!["fan"]!.GetValue<int>());
        Assert.Equal("turbo", response.Data["mode"]!.GetValue<string>());
        Assert.False(response.Data["mux"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AFailureResponseKeepsItsCodeAndMessage()
    {
        var bytes = await WrittenAsync(s => WmiWorkerProtocol.WriteAsync(
            s, WmiWorkerResponse.Failure(WmiOperationResult.CodeWmiFailed, "the firmware said no")));

        using var stream = new MemoryStream(bytes, writable: false);
        var response = await WmiWorkerProtocol.ReadResponseAsync(stream);

        Assert.NotNull(response);
        Assert.False(response!.Ok);
        Assert.Equal(WmiOperationResult.CodeWmiFailed, response.ErrorCode);
        Assert.Equal("the firmware said no", response.Error);
        Assert.Equal(WmiOperationResult.CodeWmiFailed, response.ToResult().ErrorCode);
    }

    [Fact]
    public async Task AnEmptyStreamReadsAsClosed()
    {
        using var stream = new MemoryStream(Array.Empty<byte>(), writable: false);

        var read = await WmiWorkerProtocol.ReadRequestAsync(stream);

        Assert.True(read.Closed);
        Assert.Null(read.Request);
        Assert.False(read.Malformed);
    }

    [Fact]
    public async Task AFrameThatIsNotJsonReadsAsMalformedRatherThanClosed()
    {
        // The worker answers a malformed frame with an error and keeps serving; only a closed pipe ends the loop.
        // Collapsing the two would turn a bad client into a dropped connection.
        using var stream = Framed("this is not json");

        var read = await WmiWorkerProtocol.ReadRequestAsync(stream);

        Assert.False(read.Closed);
        Assert.Null(read.Request);
        Assert.True(read.Malformed);
    }

    [Fact]
    public async Task ATruncatedPayloadReadsAsClosed()
    {
        var frame = WmiWorkerProtocol.Frame("{\"type\":\"command\",\"command\":2}");
        using var stream = new MemoryStream(frame.AsSpan(0, frame.Length - 5).ToArray(), writable: false);

        var read = await WmiWorkerProtocol.ReadRequestAsync(stream);

        Assert.True(read.Closed);
        Assert.Null(read.Request);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(WmiWorkerProtocol.MaxFrameBytes + 1)]
    [InlineData(int.MaxValue)]
    public async Task ALengthPrefixOutsideTheCapIsRefusedWithoutAllocatingIt(int length)
    {
        // The cap is what stops a hostile peer from making the elevated worker allocate a gigabyte by promising one.
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header, writable: false);

        var read = await WmiWorkerProtocol.ReadRequestAsync(stream);

        Assert.True(read.Closed);
        Assert.Null(read.Request);
    }

    [Fact]
    public async Task TheStringPathStillReadsWhatTheBytePathWrote()
    {
        // Both paths share one framing implementation, so a message written by either has to be readable by the
        // other. This is the test that would fail if the two ever drifted apart.
        var bytes = await WrittenAsync(s => WmiWorkerProtocol.WriteAsync(s, SampleRequest()));

        using var stream = new MemoryStream(bytes, writable: false);
        var json = await WmiWorkerProtocol.ReadAsync(stream);

        Assert.NotNull(json);
        var request = WmiWorkerProtocol.DeserializeRequest(json!);
        Assert.NotNull(request);
        Assert.Equal(WmiWorkerCommand.ReadTelemetry, request!.Command);
    }

    [Fact]
    public async Task WritingARequestDoesNotAllocateAPayloadAndAFrameArray()
    {
        var request = SampleRequest();

        // Warm up: the first serialize for a type builds and caches its metadata.
        await WrittenAsync(s => WmiWorkerProtocol.WriteAsync(s, request));

        using var sink = new MemoryStream(4096);
        const int Messages = 200;

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Messages; i++)
        {
            sink.Position = 0;
            await WmiWorkerProtocol.WriteAsync(sink, request);
        }

        var perMessage = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Messages;

        // The old path allocated the JSON string, the UTF-8 payload and the frame array: 432 bytes for this message,
        // measured the same way. What is left is the writer object and the serializer's own bookkeeping.
        Assert.True(perMessage < 300, $"writing a request allocated {perMessage:F0} bytes per message");
    }

    [Fact]
    public async Task ReadingARequestDoesNotAllocateTheJsonString()
    {
        var bytes = await WrittenAsync(s => WmiWorkerProtocol.WriteAsync(s, SampleRequest()));

        // Warm up both paths so the comparison is about the framing, not about first-call metadata.
        await ReadThroughStringAsync(bytes);
        await ReadThroughBytesAsync(bytes);

        const int Messages = 200;

        var beforeString = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Messages; i++) await ReadThroughStringAsync(bytes);
        var stringPath = (GC.GetAllocatedBytesForCurrentThread() - beforeString) / (double)Messages;

        var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Messages; i++) await ReadThroughBytesAsync(bytes);
        var bytePath = (GC.GetAllocatedBytesForCurrentThread() - beforeBytes) / (double)Messages;

        // Both paths build the same request object, so the difference is the string the old one decoded the frame
        // into and then parsed: 896 bytes against 704 for this message. The byte path has to be meaningfully
        // cheaper, not merely different.
        Assert.True(bytePath < stringPath * 0.9,
            $"byte path {bytePath:F0} bytes vs string path {stringPath:F0} bytes per message");
    }

    private static async Task ReadThroughStringAsync(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var json = await WmiWorkerProtocol.ReadAsync(stream);
        Assert.NotNull(WmiWorkerProtocol.DeserializeRequest(json!));
    }

    private static async Task ReadThroughBytesAsync(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var read = await WmiWorkerProtocol.ReadRequestAsync(stream);
        Assert.NotNull(read.Request);
    }
}
