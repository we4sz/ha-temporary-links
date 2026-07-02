using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TemporaryLinks.Addon.Configuration;

namespace TemporaryLinks.Addon.Services;

public class HaEventListenerService(
    IServiceProvider serviceProvider,
    ILogger<HaEventListenerService> logger,
    IOptions<AddonConfiguration> config)
    : BackgroundService
{
    private readonly AddonConfiguration _config = config.Value;
    private ClientWebSocket? _webSocket;
    private int _messageId = 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting Home Assistant event listener");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in event listener, will retry in 10 seconds");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        logger.LogInformation("Home Assistant event listener stopped");
    }

    private async Task ConnectAndListenAsync(CancellationToken stoppingToken)
    {   
        var haUrl = _config.HaUrl.Replace("http://", "ws://").Replace("https://", "wss://").TrimEnd('/');
        var wsUri = $"{haUrl}/api/websocket";

        _webSocket = new ClientWebSocket();

        logger.LogInformation("Connecting to HA WebSocket at {Uri}", wsUri);
        await _webSocket.ConnectAsync(new Uri(wsUri), stoppingToken);
        logger.LogInformation("Connected to HA WebSocket");

        var buffer = new byte[4096];
        var messageBuilder = new StringBuilder();

        while (_webSocket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
        {
            var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                logger.LogWarning("WebSocket closed by server");
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", stoppingToken);
                break;
            }

            var messageChunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
            messageBuilder.Append(messageChunk);

            if (result.EndOfMessage)
            {
                var message = messageBuilder.ToString();
                messageBuilder.Clear();

                await HandleMessageAsync(message, stoppingToken);
            }
        }
    }

    private async Task HandleMessageAsync(string message, CancellationToken stoppingToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            var type = root.GetProperty("type").GetString();

            if (type == "auth_required")
            {
                logger.LogInformation("Authentication required");
                await AuthenticateAsync(stoppingToken);
            }
            else if (type == "auth_ok")
            {
                logger.LogInformation("Authentication successful");
                await SubscribeToEventsAsync(stoppingToken);
            }
            else if (type == "auth_invalid")
            {
                logger.LogError("Authentication failed");
                throw new InvalidOperationException("WebSocket authentication failed");
            }
            else if (type == "event")
            {
                await HandleEventAsync(root, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling WebSocket message: {Message}", message);
        }
    }

    private async Task AuthenticateAsync(CancellationToken stoppingToken)
    {
        var authMessage = new
        {
            type = "auth",
            access_token = _config.HaToken
        };

        await SendMessageAsync(authMessage, stoppingToken);
    }
    
    
    private int GetNextMessageId() => Interlocked.Increment(ref _messageId);

    private async Task SubscribeToEventsAsync(CancellationToken stoppingToken)
    {
        var subscribeMessage = new
        {
            id = GetNextMessageId(),
            type = "subscribe_events",
            event_type = "temp_link_triggered"
        };

        await SendMessageAsync(subscribeMessage, stoppingToken);
        logger.LogInformation("Subscribed to temp_link_triggered events");
    }

    private async Task HandleEventAsync(JsonElement eventMessage, CancellationToken stoppingToken)
    {
        try
        {
            if (!eventMessage.TryGetProperty("event", out var eventElement))
                return;

            if (!eventElement.TryGetProperty("event_type", out var eventTypeElement) ||
                eventTypeElement.GetString() != "temp_link_triggered")
                return;

            if (!eventElement.TryGetProperty("data", out var dataElement))
                return;

            if (!dataElement.TryGetProperty("token", out var tokenElement))
                return;

            var token = tokenElement.GetString();
            if (string.IsNullOrEmpty(token))
                return;

            logger.LogInformation("Received temp_link_triggered event");

            using var scope = serviceProvider.CreateScope();
            var linkService = scope.ServiceProvider.GetRequiredService<ILinkService>();

            var result = await linkService.ExecuteLinkAsync(token, "webhook", "Home Assistant Webhook");

            logger.LogInformation("Link execution result: {Status}", result.Status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling temp_link_triggered event");
        }
    }

    private async Task SendMessageAsync(object message, CancellationToken stoppingToken)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not connected");

        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            stoppingToken);
    }

    public override void Dispose()
    {
        _webSocket?.Dispose();
        base.Dispose();
    }
}
