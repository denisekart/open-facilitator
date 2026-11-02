// Hand-written SignalR JSON-protocol probe over a raw ClientWebSocket. The
// version-negotiation leg must speak to /hubs/link exactly like a foreign
// client would — no Microsoft SignalR client library — so the runner ships
// its own minimal codec: handshake `{protocol:"json",version:1}`, 0x1E-
// delimited frames, type 1 invocation / 6 completion / 7 close.

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ofac.Conformance;

/// <summary>Outcome of a hello-offering probe.</summary>
/// <param name="Goodbye">The pushed Disconnect(goodbye) frame, when the kernel refused the version set.</param>
/// <param name="Welcome">The Hello completion's WelcomeFrame, when negotiation succeeded.</param>
public sealed record NegotiationProbe(JsonNode? Goodbye, JsonNode? Welcome);

public static class SignalRProbe
{
    private const char Separator = '\u001e';

    /// <summary>Connect, handshake, invoke Hello with <paramref name="helloFrame"/>,
    /// and read until the kernel either completes the invocation (welcome) or
    /// pushes a Disconnect(goodbye) invocation plus the empty-version completion.</summary>
    public static async Task<NegotiationProbe> HelloProbeAsync(
        string baseUrl, string linkToken, JsonNode helloFrame, int timeoutSeconds, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var socket = new ClientWebSocket();
        // The SignalR JSON protocol negotiates the "json.framing.v1" subprotocol.
        socket.Options.AddSubProtocol("json.framing.v1");
        // Bearer on the upgrade header — the hub's scheme reads Authorization
        // first and only falls back to ?access_token for the upgrade request.
        socket.Options.SetRequestHeader("Authorization", $"Bearer {linkToken}");
        var uri = new UriBuilder(baseUrl)
        {
            Scheme = baseUrl.StartsWith("https", StringComparison.Ordinal) ? "wss" : "ws",
            Path = "/hubs/link",
        }.Uri;
        await socket.ConnectAsync(uri, timeout.Token);

        await SendAsync(socket, """{"protocol":"json","version":1}""", timeout.Token);

        var buffer = new byte[64 * 1024];
        var text = new StringBuilder();
        var handshaked = false;
        JsonNode? goodbye = null;
        JsonNode? welcome = null;
        while (welcome is null && goodbye is null)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
            {
                continue;
            }

            foreach (var frame in text.ToString().Split(Separator, StringSplitOptions.RemoveEmptyEntries))
            {
                JsonNode? message;
                try
                {
                    message = JsonNode.Parse(frame);
                }
                catch (JsonException)
                {
                    continue;
                }

                var type = message?["type"]?.GetValue<int>() ?? -1;
                // The handshake ACK is a completion frame with an EMPTY
                // invocationId; only after it may Hello be invoked.
                var invocationId = message?["invocationId"]?.GetValue<string>();
                if (!handshaked && (type == 0 || (type == 6 && invocationId is "" or null)))
                {
                    handshaked = true;
                    await SendAsync(socket,
                        """{"type":1,"invocationId":"1","target":"Hello","arguments":["""
                        + helloFrame.ToJsonString() + "]}", timeout.Token);
                }
                else if (type == 1 && message?["target"]?.GetValue<string>() == "Disconnect")
                {
                    goodbye = message["arguments"]?[0]?.DeepClone();
                }
                else if (type == 6 && message?["invocationId"]?.GetValue<string>() == "1")
                {
                    welcome = message["result"]?.DeepClone();
                }
            }

            text.Clear();
        }

        return new NegotiationProbe(goodbye, welcome);
    }

    private static async Task SendAsync(ClientWebSocket socket, string payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload + Separator);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }
}
