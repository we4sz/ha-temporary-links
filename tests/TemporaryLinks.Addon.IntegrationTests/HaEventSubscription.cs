using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TemporaryLinks.Addon.IntegrationTests;

/// <summary>
/// A live subscription to one HA event type over the WebSocket API. Opening the
/// subscription completes only after HA confirms it, so an event fired immediately
/// afterwards cannot be missed.
/// </summary>
public sealed class HaEventSubscription : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveLoop;

    private HaEventSubscription(ClientWebSocket socket)
    {
        _socket = socket;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public static async Task<HaEventSubscription> OpenAsync(string haUrl, string token, string eventType)
    {
        var wsUrl = haUrl.Replace("https://", "wss://").Replace("http://", "ws://") + "/api/websocket";
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

        await ExpectTypeAsync(socket, "auth_required");
        await SendAsync(socket, new { type = "auth", access_token = token });
        await ExpectTypeAsync(socket, "auth_ok");

        await SendAsync(socket, new { id = 1, type = "subscribe_events", event_type = eventType });
        var result = await ExpectTypeAsync(socket, "result");
        if (!result.GetProperty("success").GetBoolean())
        {
            throw new InvalidOperationException($"subscribe_events failed: {result}");
        }

        return new HaEventSubscription(socket);
    }

    /// <summary>The first pending event matching the predicate, or null if none arrives in time.</summary>
    public async Task<JsonElement?> WaitForMatchAsync(Func<JsonElement, bool> matches, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var evt = await _events.Reader.ReadAsync(timeoutCts.Token);
                if (matches(evt))
                {
                    return evt;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var message = await ReceiveMessageAsync(_socket, _cts.Token);
                if (message.TryGetProperty("type", out var type) && type.GetString() == "event")
                {
                    _events.Writer.TryWrite(
                        message.GetProperty("event").GetProperty("data").Clone());
                }
            }
        }
        catch
        {
            // Socket closed or cancelled — the channel simply stops producing.
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<JsonElement> ExpectTypeAsync(ClientWebSocket socket, string expected)
    {
        var message = await ReceiveMessageAsync(socket, CancellationToken.None);
        var actual = message.GetProperty("type").GetString();
        if (actual != expected)
        {
            throw new InvalidOperationException($"Expected WS message '{expected}', got: {message}");
        }
        return message;
    }

    private static async Task<JsonElement> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new OperationCanceledException("WebSocket closed by Home Assistant.");
            }
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch
        {
            // Best effort — the test is over either way.
        }
        await Task.WhenAny(_receiveLoop, Task.Delay(1000));
        _socket.Dispose();
        _cts.Dispose();
    }
}
