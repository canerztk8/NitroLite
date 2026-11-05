# Handoff Report: Requirements R3 & R4 Audit and Implementation Survey

## Executive Summary
This report provides the technical investigation, root cause analysis, architecture design, and unit testing strategy for:
- **Requirement R3**: Acer Agent Client Framing & Robust Communication (`NitroLite/Core/AcerAgentClient.cs`)
- **Requirement R4**: NVIDIA NVML Secure DLL Search Path (`NitroLite/Core/HardwareMonitorService.cs`)

---

## 1. Observation

### 1.1 Acer Agent Client (`NitroLite/Core/AcerAgentClient.cs`)
Direct inspection of `AcerAgentClient.cs` (lines 24–166) reveals several critical issues in socket I/O, framing, memory allocation, and connection lifecycle management:

```csharp
// AcerAgentClient.cs lines 26-27: Hardcoded host and port prevents local mock testing
private const string Host = "127.0.0.1";
private const int Port = 46933;

// AcerAgentClient.cs lines 101-144: Socket reading and framing loop
// Read response with framing and accumulation to handle chunking safely
using var responseMs = new MemoryStream();
var buffer = new byte[8192];

while (true)
{
    int read = await _stream.ReadAsync(buffer, 0, buffer.Length, ioCts.Token);
    if (read <= 0)
    {
        if (responseMs.Length > 0) break;
        Disconnect();
        return null;
    }

    responseMs.Write(buffer, 0, read);
    var currentStr = Encoding.UTF8.GetString(responseMs.ToArray()).Trim();

    if (IsValidJson(currentStr))
    {
        return currentStr;
    }

    if (!_stream.DataAvailable)
    {
        if (IsValidJson(currentStr))
        {
            return currentStr;
        }

        if (currentStr.StartsWith("{") && !currentStr.EndsWith("}"))
        {
            await Task.Delay(25, ioCts.Token);
            if (!_stream.DataAvailable) break;
        }
        else
        {
            break;
        }
    }
}

string result = Encoding.UTF8.GetString(responseMs.ToArray()).Trim();
return string.IsNullOrEmpty(result) ? null : result;
```

#### Observed Deficiencies in `AcerAgentClient.cs`:
1. **Premature 25ms Timeout and Truncated JSON (Lines 130–139)**:
   When `_stream.DataAvailable` is false (meaning no further bytes are currently waiting in the OS socket receive buffer), the code executes `await Task.Delay(25, ioCts.Token)`. If another chunk arrives after 30ms or 50ms (common over Windows IPC sockets or under CPU load), `!_stream.DataAvailable` evaluates to true after just 25ms, breaking the loop (`break;`). At lines 142–143, the incomplete buffer is converted to a string and returned as valid output. Downstream callers like `GetMonitorDataAsync()` execute `JsonNode.Parse(resp)`, which throws `JsonException` on truncated JSON.
2. **Subsequent Command Contamination by Late-Arriving Bytes**:
   When the read loop breaks early due to the 25ms cutoff, the remainder of the response packet is still in flight over TCP. When the next command (e.g. `GetOperatingModeAsync()`) is issued, its `ReadAsync` immediately consumes the leftover bytes of the previous command, causing desynchronization and cascading failures for all subsequent commands.
3. **No Connection Teardown or Draining on Incomplete/Malformed Reads**:
   If the loop breaks out on incomplete data, `Disconnect()` is NOT invoked. The socket remains connected in an inconsistent state. Furthermore, before sending a new command in `SendRawAsync`, `_stream` is not drained of existing unread bytes.
4. **Unbounded Response Buffer (Denial of Service / OOM Risk)**:
   `responseMs` has no size limitation. If a rogue service or malformed stream transmits continuous data, memory usage grows unbounded until `OutOfMemoryException`.
5. **Quadratic O(N^2) Allocations via Buffer Re-Copying (Lines 115–116)**:
   On every chunk read, `responseMs.ToArray()` allocates a brand-new byte array copying all accumulated bytes, `Encoding.UTF8.GetString()` decodes all accumulated bytes into a new heap string, and `IsValidJson()` parses the string again. For a multi-chunk packet, this wastes CPU cycles and generates massive garbage collection pressure.
6. **Hardcoded Port 46933 Prevents Testability**:
   `Host` and `Port` are `private const`, meaning unit tests cannot instantiate `AcerAgentClient` against a local test `TcpListener` on `127.0.0.1:0`.
7. **Existing Test Coverage Gap (`NitroLite.Tests/AcerAgentClientTests.cs`)**:
   `AcerAgentClientTests.cs` lines 43–76 test an isolated local `MemoryStream` object, and do NOT test `AcerAgentClient`, its socket I/O, fragmentation handling, timeout cutoff, or socket draining.

---

### 1.2 NVIDIA NVML P/Invoke Search Path (`NitroLite/Core/HardwareMonitorService.cs`)
Direct inspection of `HardwareMonitorService.cs` (lines 8–38) reveals:

```csharp
// HardwareMonitorService.cs lines 8-38
private const string NvmlDll = "nvml.dll";

[DllImport(NvmlDll, EntryPoint = "nvmlInit_v2")]
private static extern int NvmlInit();

[DllImport(NvmlDll, EntryPoint = "nvmlShutdown")]
private static extern int NvmlShutdown();

[DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
private static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

[DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetPowerUsage")]
private static extern int NvmlDeviceGetPowerUsage(IntPtr device, out uint powerMilliwatts);

[StructLayout(LayoutKind.Sequential)]
private struct NvmlMemory
{
    public ulong Total;
    public ulong Free;
    public ulong Used;
}

[DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetMemoryInfo")]
private static extern int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

[DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetClockInfo")]
private static extern int NvmlDeviceGetClockInfo(IntPtr device, uint clockType, out uint clockMHz);

[DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetTemperature")]
private static extern int NvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temp);
```

#### Observed Deficiencies in `HardwareMonitorService.cs`:
1. **Unpinned DLL Loading / Search Path Hijacking**:
   The P/Invoke declarations specify relative library name `"nvml.dll"` without `[DefaultDllImportSearchPaths]` or explicit path resolution.
2. **Windows Default DLL Search Order Vulnerability**:
   Under standard Windows desktop DLL resolution order, Windows searches:
   - Application directory (`AppDomain.CurrentDomain.BaseDirectory`)
   - System directory (`%SystemRoot%\System32`)
   - 16-bit system directory
   - Windows directory (`%SystemRoot%`)
   - Current working directory (CWD)
   - Directories listed in `%PATH%`
   If an untrusted actor or unprivileged process plants a rogue `nvml.dll` in the current working directory or application folder, NitroLite will load and execute native code from that unpinned DLL upon startup or telemetry polling.
3. **Existing Test Coverage Gap**:
   There are zero unit tests verifying that `nvml.dll` P/Invoke imports specify `DefaultDllImportSearchPaths` or restrict loading to trusted locations.

---

## 2. Logic Chain

### 2.1 Remediation Logic for Requirement R3 (Acer Agent Client)

#### Step 1: Eliminating the 25ms Timeout and Polling Bug
- In TCP, network packets may be fragmented across segments due to MTU boundaries, TCP windowing, or thread scheduling on the server.
- The client should NOT poll `_stream.DataAvailable` and break after 25ms. Instead, it must asynchronously await `_stream.ReadAsync` until either a complete JSON object is confirmed, the operation times out (e.g. 3000ms via `CancellationTokenSource`), or the connection is terminated by the host (`read <= 0`).
- If an overall timeout or cancellation occurs, the operation throws `OperationCanceledException`, which is caught, triggers `Disconnect()`, and returns `null`.

#### Step 2: Zero Re-Copying Buffer Accumulation
- Allocate or rent a single buffer of `MaxResponseBytes` (e.g. 256 KB, using `ArrayPool<byte>.Shared.Rent`).
- Read each socket chunk directly into `rentedBuffer.AsMemory(totalRead, spaceLeft)`.
- Advance `totalRead += read`. No intermediate byte arrays or strings are created during chunk accumulation.

#### Step 3: Fast Framing & Zero-Allocation JSON Validation
- Locate the first non-whitespace byte in `rentedBuffer[0..totalRead]`. If it is not `{` or `[`, the data is malformed; disconnect immediately.
- Locate the last non-whitespace byte in `rentedBuffer[0..totalRead]`. If it is not `}` or `]`, the payload cannot be complete; continue reading without parsing.
- When delimiters match, validate the candidate JSON slice `ReadOnlyMemory<byte>(rentedBuffer, start, length)` using `JsonDocument.Parse()`.
- If `JsonDocument.Parse` succeeds and the root element is an object or array, the response is complete. Decode into string once via `Encoding.UTF8.GetString()` and return.
- If `JsonException` is thrown (e.g. an inner brace matched but the outer object remains unclosed), continue reading further chunks until the outer object completes.

#### Step 4: Strict Upper Bound Enforcement
- Define `public const int DefaultMaxResponseBytes = 256 * 1024` (256 KB) with configurable property `MaxResponseBytes`.
- If `totalRead` reaches `MaxResponseBytes` and the payload is still incomplete, immediately abort reading, call `Disconnect()`, and return `null`.

#### Step 5: Connection Teardown and Socket Draining
- If an incomplete read, EOF before completion, malformed payload, or timeout occurs, `Disconnect()` is called immediately to close and dispose the socket.
- When `EnsureConnectedAsync()` is subsequently called, a fresh TCP connection is established, ensuring no lingering bytes from the failed transaction can ever leak into subsequent commands.
- Before transmitting any new command packet in `SendRawAsync`, inspect `_stream.DataAvailable`. If any stray bytes exist in the receive buffer, drain them asynchronously before writing the new request.

#### Step 6: Testability and Constructor Inversion
- Introduce `public AcerAgentClient(string host = DefaultHost, int port = DefaultPort)` with default arguments matching production values `127.0.0.1:46933`.
- This enables unit tests to spin up an in-process loopback `TcpListener` on port `0` and inject its port into `AcerAgentClient`, without modifying production callers.

---

### 2.2 Remediation Logic for Requirement R4 (NVML Secure Search Path)

#### Step 1: Pinning Search Paths via `DefaultDllImportSearchPathsAttribute`
- Applying `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` to each P/Invoke method instructs the Windows loader (via `LoadLibraryExW` with `LOAD_LIBRARY_SEARCH_SYSTEM32`) to look **only** in `%SystemRoot%\System32`.
- This completely excludes the current working directory, application directory, and PATH environment directories from the DLL resolution path.
- In Windows 10 and 11, NVIDIA DCH display drivers install `nvml.dll` into `C:\Windows\System32\nvml.dll`.

#### Step 2: Explicit Secure Path Resolution Fallback
- For compatibility with legacy enterprise or non-DCH installations where `nvml.dll` resides under `C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll`, register a secure `DllImportResolver` via `NativeLibrary.SetDllImportResolver` in `HardwareMonitorService`'s static constructor.
- The resolver checks only trusted absolute paths:
  1. `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll")`
  2. `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll")`
- If found in either trusted location, it loads via `NativeLibrary.Load()`. If neither exists, it returns `IntPtr.Zero`, preventing unpinned lookups.

#### Step 3: P/Invoke Reflection Verification
- Using .NET reflection, unit tests can enumerate all P/Invoke methods on `HardwareMonitorService` that import `nvml.dll` (and `kernel32.dll`), validating that `DefaultDllImportSearchPathsAttribute` is present and set to `DllImportSearchPath.System32`.

---

## 3. Caveats

1. **AASSvc Protocol Idiosyncrasy**:
   Acer Agent Service (`AASSvc`) does not prepend a length prefix to its JSON response packets (unlike its request packets which have an 8-byte `Magic + PacketId` header). The response is raw UTF-8 JSON. Framing must therefore rely on balanced JSON delimiters and `JsonDocument` validation rather than a fixed byte count.
2. **Reentrancy and Concurrency**:
   `AcerAgentClient` uses a non-reentrant `SemaphoreSlim(1, 1)` to serialize requests. Public methods hold this lock. Internal methods must not re-acquire `_lock` to avoid deadlocks.
3. **Testing Without Real Hardware**:
   In CI or non-NVIDIA test environments, `nvml.dll` may not be present in `System32`. `HardwareMonitorService.TryInitNvml()` already catches `DllNotFoundException` gracefully. Tests verify search path pinning via reflection and architectural constraints rather than expecting a physical GPU device handle.
4. **Assembly Metadata Visibility**:
   `NitroLite/AssemblyInfo.cs` should include `[assembly: InternalsVisibleTo("NitroLite.Tests")]` so test suites can directly inspect internal methods like `SendRawAsync`, `TryValidateJson`, and `Disconnect`.

---

## 4. Conclusion & Proposed Implementation Plan

### 4.1 Detailed Code Changes for `NitroLite/Core/AcerAgentClient.cs`

```csharp
// Proposed replacement for NitroLite/Core/AcerAgentClient.cs
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NitroLite.Core;

public class AcerAgentClient : IDisposable
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 46933;
    public const int DefaultMaxResponseBytes = 256 * 1024; // 256 KB

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ACER");

    // Packet IDs
    private const uint PacketInitialization = 0;
    private const uint PacketGetMonitorData = 10;
    private const uint PacketGetUpdatedData = 20;
    private const uint PacketGetConnectedDevice = 30;
    private const uint PacketGetLightingCapability = 40;
    private const uint PacketSetDeviceData = 100;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _host;
    private readonly int _port;
    private int _maxResponseBytes = DefaultMaxResponseBytes;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public virtual bool IsConnected => _client?.Connected ?? false;

    public int MaxResponseBytes
    {
        get => _maxResponseBytes;
        set => _maxResponseBytes = Math.Max(1024, value);
    }

    public AcerAgentClient(string host = DefaultHost, int port = DefaultPort)
    {
        _host = host;
        _port = port;
    }

    internal void Disconnect()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
    }

    public virtual async Task<bool> EnsureConnectedAsync()
    {
        if (_client != null && _client.Connected)
            return true;

        Disconnect();

        try
        {
            _client = new TcpClient();
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _client.ConnectAsync(_host, _port, connectCts.Token);
            _stream = _client.GetStream();
            _stream.ReadTimeout = 3000;
            _stream.WriteTimeout = 3000;

            // Send handshake initialization
            var initResp = await SendRawAsync(PacketInitialization, new { Function = "VERSION" });
            return initResp != null;
        }
        catch
        {
            Disconnect();
            return false;
        }
    }

    internal virtual async Task<string?> SendRawAsync(uint packetId, object payload, CancellationToken cancellationToken = default)
    {
        if (_stream == null || _client == null || !_client.Connected) return null;

        try
        {
            using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ioCts.CancelAfter(TimeSpan.FromSeconds(3));

            // 1. Drain any unexpected lingering bytes in the stream to prevent contaminating the new request
            if (_stream.DataAvailable)
            {
                byte[] drainBuf = new byte[1024];
                while (_stream.DataAvailable)
                {
                    int drained = await _stream.ReadAsync(drainBuf.AsMemory(0, drainBuf.Length), ioCts.Token);
                    if (drained <= 0) break;
                }
            }

            // 2. Format and send the packet: Magic ("ACER") + packetId (uint32 LE) + payload (UTF-8 JSON)
            var json = JsonSerializer.Serialize(payload);
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            var packetIdBytes = BitConverter.GetBytes(packetId);

            int totalPacketLen = Magic.Length + packetIdBytes.Length + jsonBytes.Length;
            byte[] packet = new byte[totalPacketLen];
            Buffer.BlockCopy(Magic, 0, packet, 0, Magic.Length);
            Buffer.BlockCopy(packetIdBytes, 0, packet, Magic.Length, packetIdBytes.Length);
            Buffer.BlockCopy(jsonBytes, 0, packet, Magic.Length + packetIdBytes.Length, jsonBytes.Length);

            await _stream.WriteAsync(packet.AsMemory(), ioCts.Token);
            await _stream.FlushAsync(ioCts.Token);

            // 3. Read response using rented buffer up to MaxResponseBytes (zero re-copying)
            int maxBytes = _maxResponseBytes;
            byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(maxBytes);
            try
            {
                int totalRead = 0;

                while (true)
                {
                    int spaceLeft = maxBytes - totalRead;
                    if (spaceLeft <= 0)
                    {
                        // Exceeded maximum response size limit!
                        Disconnect();
                        return null;
                    }

                    int read = await _stream.ReadAsync(rentedBuffer.AsMemory(totalRead, spaceLeft), ioCts.Token);
                    if (read <= 0)
                    {
                        // Remote connection closed prematurely before complete JSON was received
                        Disconnect();
                        return null;
                    }

                    totalRead += read;

                    // Locate JSON boundaries in the accumulated buffer
                    int start = 0;
                    while (start < totalRead && IsAsciiWhitespace(rentedBuffer[start]))
                    {
                        start++;
                    }

                    if (start >= totalRead)
                    {
                        // Whitespace only so far; continue reading
                        continue;
                    }

                    // A valid response must begin with '{' or '['
                    if (rentedBuffer[start] != (byte)'{' && rentedBuffer[start] != (byte)'[')
                    {
                        // Malformed non-JSON data received; close connection to prevent corruption
                        Disconnect();
                        return null;
                    }

                    int end = totalRead - 1;
                    while (end >= start && IsAsciiWhitespace(rentedBuffer[end]))
                    {
                        end--;
                    }

                    byte expectedClose = rentedBuffer[start] == (byte)'{' ? (byte)'}' : (byte)']';
                    if (rentedBuffer[end] != expectedClose)
                    {
                        // Fragment is incomplete (does not end with closing delimiter); continue reading
                        continue;
                    }

                    // Delimiters match candidate complete JSON; validate syntax without string allocation
                    int candidateLength = end - start + 1;
                    if (TryValidateJson(new ReadOnlyMemory<byte>(rentedBuffer, start, candidateLength)))
                    {
                        // Complete and valid JSON received! Single string allocation here.
                        return Encoding.UTF8.GetString(rentedBuffer, start, candidateLength);
                    }

                    // Delimiter matched but inner syntax is incomplete (e.g. nested object); continue reading
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }
        catch
        {
            // Socket timeout, cancellation, or I/O error; close connection immediately
            Disconnect();
            return null;
        }
    }

    private static bool IsAsciiWhitespace(byte b) =>
        b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n';

    internal static bool TryValidateJson(ReadOnlyMemory<byte> memory)
    {
        try
        {
            using var doc = JsonDocument.Parse(memory);
            return doc.RootElement.ValueKind == JsonValueKind.Object ||
                   doc.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsValidJson(string? str)
    {
        if (string.IsNullOrWhiteSpace(str)) return false;
        str = str.Trim();
        if (!str.StartsWith("{") || !str.EndsWith("}")) return false;
        try
        {
            using var doc = JsonDocument.Parse(str);
            return true;
        }
        catch
        {
            return false;
        }
    }
...
```

---

### 4.2 Detailed Code Changes for `NitroLite/Core/HardwareMonitorService.cs`

```csharp
// Changes to NitroLite/Core/HardwareMonitorService.cs lines 8-40
    private const string NvmlDll = "nvml.dll";

    static HardwareMonitorService()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(HardwareMonitorService).Assembly, ResolveSecureDll);
        }
        catch (InvalidOperationException)
        {
            // Resolver already configured for this assembly
        }
    }

    private static IntPtr ResolveSecureDll(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (string.Equals(libraryName, NvmlDll, StringComparison.OrdinalIgnoreCase))
        {
            // 1. Primary: Trusted Windows System32 directory (Standard DCH driver location)
            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string system32Path = Path.Combine(sysDir, NvmlDll);
            if (File.Exists(system32Path) && NativeLibrary.TryLoad(system32Path, out IntPtr handle))
            {
                return handle;
            }

            // 2. Secondary: Trusted Program Files NVSMI directory (Legacy NVIDIA driver location)
            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string nvsmiPath = Path.Combine(progFiles, "NVIDIA Corporation", "NVSMI", NvmlDll);
            if (File.Exists(nvsmiPath) && NativeLibrary.TryLoad(nvsmiPath, out handle))
            {
                return handle;
            }

            // Refuse to search or load from ApplicationDirectory, CurrentDirectory, or PATH!
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    [DllImport(NvmlDll, EntryPoint = "nvmlInit_v2")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NvmlInit();

    [DllImport(NvmlDll, EntryPoint = "nvmlShutdown")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NvmlShutdown();

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetPowerUsage")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NvmlDeviceGetPowerUsage(IntPtr device, out uint powerMilliwatts);

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetMemoryInfo")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetClockInfo")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NvmlDeviceGetClockInfo(IntPtr device, uint clockType, out uint clockMHz);

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetTemperature")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temp);

    // Native Windows APIs for RAM & CPU fallbacks
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);
```

---

### 4.3 Detailed Unit Test Strategy for `NitroLite.Tests`

Add comprehensive unit tests to `NitroLite.Tests/AcerAgentClientTests.cs` and a new test class `NitroLite.Tests/HardwareMonitorSecurityTests.cs`:

#### Test 1: Multi-Chunk Fragmentation with >25ms Artificial Delays
```csharp
[Fact]
public async Task AcerAgentClient_FragmentedMultiChunkResponse_ReassembledCorrectlyWithoutPrematureTimeout()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;

    var serverTask = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();

        // 1. Handshake response
        byte[] buffer = new byte[1024];
        int read = await stream.ReadAsync(buffer, 0, buffer.Length);
        byte[] handshakeResp = Encoding.UTF8.GetBytes("{\"result\": \"0\", \"version\": \"1.0.0\"}");
        await stream.WriteAsync(handshakeResp);

        // 2. Command request
        read = await stream.ReadAsync(buffer, 0, buffer.Length);

        // Send response across 3 fragments separated by 60ms delays (> 25ms)
        byte[] chunk1 = Encoding.UTF8.GetBytes("{\"result\": \"0\", \"data\": {\"CpuTemperature\": 68, ");
        byte[] chunk2 = Encoding.UTF8.GetBytes("\"GpuTemperature\": 55, \"CpuFanSpeed\": 2500, ");
        byte[] chunk3 = Encoding.UTF8.GetBytes("\"GpuFanSpeed\": 2400}}");

        await stream.WriteAsync(chunk1);
        await Task.Delay(60);
        await stream.WriteAsync(chunk2);
        await Task.Delay(60);
        await stream.WriteAsync(chunk3);
    });

    try
    {
        using var agentClient = new AcerAgentClient("127.0.0.1", port);
        var monitorData = await agentClient.GetMonitorDataAsync();

        Assert.NotNull(monitorData);
        Assert.Equal(68, monitorData.CpuTemperature);
        Assert.Equal(55, monitorData.GpuTemperature);
        Assert.Equal(2500, monitorData.CpuFanSpeed);
        Assert.Equal(2400, monitorData.GpuFanSpeed);
    }
    finally
    {
        listener.Stop();
        await serverTask;
    }
}
```

#### Test 2: Late-Arriving Bytes and Stray Stream Draining
```csharp
[Fact]
public async Task AcerAgentClient_LateOrLingeringBytes_DoNotContaminateSubsequentCommands()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;

    var serverTask = Task.Run(async () =>
    {
        // Connection 1 (will experience incomplete read and disconnect)
        using var client1 = await listener.AcceptTcpClientAsync();
        using var stream1 = client1.GetStream();

        byte[] buf = new byte[1024];
        await stream1.ReadAsync(buf, 0, buf.Length); // Handshake
        await stream1.WriteAsync(Encoding.UTF8.GetBytes("{\"result\": \"0\"}"));

        await stream1.ReadAsync(buf, 0, buf.Length); // Command 1
        // Send partial response then close connection
        await stream1.WriteAsync(Encoding.UTF8.GetBytes("{\"result\": \"0\", \"data\": {"));
        client1.Close();

        // Connection 2 (command 2 reconnects cleanly)
        using var client2 = await listener.AcceptTcpClientAsync();
        using var stream2 = client2.GetStream();
        await stream2.ReadAsync(buf, 0, buf.Length); // Handshake
        await stream2.WriteAsync(Encoding.UTF8.GetBytes("{\"result\": \"0\"}"));

        await stream2.ReadAsync(buf, 0, buf.Length); // Command 2
        await stream2.WriteAsync(Encoding.UTF8.GetBytes("{\"result\": \"0\", \"data\": {\"mode\": 2}}"));
    });

    try
    {
        using var agentClient = new AcerAgentClient("127.0.0.1", port);
        
        // Command 1 fails on incomplete read
        var mode1 = await agentClient.GetOperatingModeAsync();
        Assert.Equal(OperatingMode.Default, mode1);

        // Command 2 must succeed on clean connection
        var mode2 = await agentClient.GetOperatingModeAsync();
        Assert.Equal(OperatingMode.Performance, mode2);
    }
    finally
    {
        listener.Stop();
        await serverTask;
    }
}
```

#### Test 3: Upper Bound on Response Sizes Enforced
```csharp
[Fact]
public async Task AcerAgentClient_ExceedingMaxResponseSize_AbortsAndDisconnects()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;

    var serverTask = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        byte[] buf = new byte[1024];
        await stream.ReadAsync(buf, 0, buf.Length); // Handshake
        await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"result\": \"0\"}"));

        await stream.ReadAsync(buf, 0, buf.Length); // Command
        // Send huge response exceeding 2KB limit
        byte[] hugeChunk = Encoding.UTF8.GetBytes("{\"result\": \"0\", \"data\": \"" + new string('A', 4096) + "\"}");
        await stream.WriteAsync(hugeChunk);
    });

    try
    {
        using var agentClient = new AcerAgentClient("127.0.0.1", port);
        agentClient.MaxResponseBytes = 1024; // Limit to 1 KB

        var data = await agentClient.GetMonitorDataAsync();
        Assert.Null(data);
        Assert.False(agentClient.IsConnected);
    }
    finally
    {
        listener.Stop();
        await serverTask;
    }
}
```

#### Test 4: NVML P/Invoke Search Path Security
```csharp
[Fact]
public void HardwareMonitorService_NvmlDllImports_SpecifySafeDllImportSearchPaths()
{
    var nvmlMethods = typeof(HardwareMonitorService)
        .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        .Where(m => m.GetCustomAttribute<DllImportAttribute>()?.Value == "nvml.dll")
        .ToList();

    Assert.NotEmpty(nvmlMethods);
    Assert.True(nvmlMethods.Count >= 7, $"Expected at least 7 NVML P/Invoke methods, found {nvmlMethods.Count}");

    foreach (var method in nvmlMethods)
    {
        var searchPathAttr = method.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
        Assert.NotNull(searchPathAttr);
        Assert.True(searchPathAttr.Paths.HasFlag(DllImportSearchPath.System32),
            $"{method.Name} must specify DllImportSearchPath.System32");
        Assert.False(searchPathAttr.Paths.HasFlag(DllImportSearchPath.ApplicationDirectory),
            $"{method.Name} must not allow ApplicationDirectory");
    }
}
```

---

## 5. Verification Method

### 5.1 Verification Commands
1. **Compilation Check**:
   ```powershell
   dotnet build NitroLite.sln
   ```
   Must compile cleanly with 0 errors and 0 warnings.
2. **Test Suite Execution**:
   ```powershell
   dotnet test NitroLite.Tests/NitroLite.Tests.csproj --logger "console;verbosity=normal"
   ```
   All existing 322 unit tests plus the new tests for R3 and R4 must pass.

### 5.2 Invalidation Conditions
- Any test failure in `NitroLite.Tests`.
- Failure of multi-chunk reassembly when chunk arrival delay is >= 50ms.
- Lingering or corrupted bytes from an incomplete command contaminating a subsequent command response.
- Response size exceeding configured upper bound without aborting and closing socket.
- Any method importing `nvml.dll` without `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`.
