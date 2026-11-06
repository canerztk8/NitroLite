using System;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NitroLite.Core;

/// <summary>
/// Battery charge mode as stored by Acer Care Center (AcerCCAgent "BatteryHealthy").
/// Values match NitroSense's BATTERY_CHARGE_MODE enum.
/// </summary>
public enum BatteryChargeMode
{
    /// <summary>Charging stops at ~80 % ("Optimized battery charging" / battery health mode).</summary>
    Optimized80 = 0,
    /// <summary>Charge to 100 %.</summary>
    Full = 1,
    /// <summary>Adaptive charging (not offered on every model).</summary>
    Adaptive = 2
}

/// <summary>
/// Minimal client for the AcerCCAgent WebSocket service (wss://localhost:4343), used the same way NitroSense
/// does to read and change the battery charge limit. The handshake proves to the agent that the caller speaks
/// its protocol: the request carries {Question, Key} encrypted with key A, and the agent answers with the
/// question encrypted with key B (AES-128-ECB, PKCS7, Base64 — CryptoJS defaults). Both keys are the constants
/// shipped inside NitroSense.
/// </summary>
/// <remarks>
/// Writing C:\ProgramData\Acer\CC\settings.json (what NitroLite used to do) does not change the limit: the agent
/// reports its own value, and that file even held the opposite value on the test machine.
/// </remarks>
public class CareCenterClient
{
    public const string Endpoint = "wss://localhost:4343";
    internal const string KeyA = "A6052DC8A6E44210";
    internal const string KeyB = "AB252AB73BED1CDB";
    internal const string Question = "AcerCareCenterFunctionalityTests_HandshakingQuestion";
    private const string BatteryHealthyCommand = "BatteryHealthy";

    private const int PacketAuth = 1;
    private const int PacketRequest = 2;
    private const int PacketResponse = 3;
    private const int PacketError = 9;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public string? LastError { get; private set; }

    public virtual async Task<BatteryChargeMode?> GetBatteryChargeModeAsync()
    {
        var result = await RunAsync(new JsonObject
        {
            ["PacketType"] = PacketRequest,
            ["Version"] = 1,
            ["Command"] = BatteryHealthyCommand,
            ["Action"] = "Get"
        });

        var value = TryGetResultValue(result);
        return value.HasValue && Enum.IsDefined(typeof(BatteryChargeMode), value.Value)
            ? (BatteryChargeMode)value.Value
            : null;
    }

    /// <summary>Sets the mode and reads it back; returns true only if the agent now reports the requested mode.</summary>
    public virtual async Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode)
    {
        var result = await RunAsync(new JsonObject
        {
            ["PacketType"] = PacketRequest,
            ["Version"] = 1,
            ["Command"] = BatteryHealthyCommand,
            ["Action"] = "Set",
            ["Param1"] = (int)mode
        });
        if (result == null) return false;

        var readBack = await GetBatteryChargeModeAsync();
        if (readBack != mode)
        {
            LastError ??= $"Care Center ayarı doğrulanamadı (okunan: {readBack?.ToString() ?? "yok"}).";
            return false;
        }
        return true;
    }

    internal static string EncryptBase64(string plain, string key)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(key);
        return Convert.ToBase64String(aes.EncryptEcb(Encoding.UTF8.GetBytes(plain), PaddingMode.PKCS7));
    }

    internal static string BuildAuthData() =>
        EncryptBase64(JsonSerializer.Serialize(new { Question, Key = KeyB }), KeyA);

    internal static string ExpectedAuthAnswer() => EncryptBase64(Question, KeyB);

    internal static int? TryGetResultValue(JsonNode? response)
    {
        try { return response?["Result"]?["Value"]?.GetValue<int>(); }
        catch { return null; }
    }

    /// <summary>Connects, performs the handshake, sends one request and returns its response (null on any failure).</summary>
    private async Task<JsonNode?> RunAsync(JsonObject request)
    {
        LastError = null;
        using var cts = new CancellationTokenSource(Timeout);
        using var ws = new ClientWebSocket();
        // The agent uses a self-signed certificate for localhost. Accept it only for this loopback endpoint.
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        try
        {
            await ws.ConnectAsync(new Uri(Endpoint), cts.Token);

            var authSession = Guid.NewGuid().ToString();
            await SendAsync(ws, new JsonObject
            {
                ["PacketType"] = PacketAuth,
                ["Session"] = authSession,
                ["Version"] = 1,
                ["Data"] = BuildAuthData()
            }, cts.Token);

            var auth = await ReceiveAsync(ws, cts.Token);
            if (auth?["PacketType"]?.GetValue<int>() != PacketAuth ||
                auth["Data"]?.GetValue<string>() != ExpectedAuthAnswer())
            {
                LastError = "Care Center el sıkışması başarısız.";
                return null;
            }

            var session = Guid.NewGuid().ToString();
            request["Session"] = session;
            await SendAsync(ws, request, cts.Token);

            while (true)
            {
                var msg = await ReceiveAsync(ws, cts.Token);
                if (msg == null)
                {
                    LastError = "Care Center bağlantısı kapandı.";
                    return null;
                }
                if (msg["Session"]?.GetValue<string>() != session) continue; // Notify / unrelated packets

                int type = msg["PacketType"]?.GetValue<int>() ?? 0;
                if (type == PacketResponse) return msg;
                if (type == PacketError)
                {
                    LastError = $"Care Center hata kodu: {msg["ErrorCode"]}";
                    return null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            LastError = "Care Center yanıt vermedi (zaman aşımı).";
            return null;
        }
        catch (Exception ex)
        {
            LastError = $"Care Center hizmetine (AcerCCAgent) bağlanılamadı: {ex.Message}";
            return null;
        }
        finally
        {
            if (ws.State == WebSocketState.Open)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
            }
        }
    }

    private static Task SendAsync(ClientWebSocket ws, JsonNode packet, CancellationToken token) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(packet.ToJsonString()), WebSocketMessageType.Text, true, token);

    private static async Task<JsonNode?> ReceiveAsync(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new System.IO.MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, token);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > 1024 * 1024) return null;
            if (r.EndOfMessage) break;
        }
        try { return JsonNode.Parse(ms.ToArray()); }
        catch { return null; }
    }
}
