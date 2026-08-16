using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TemporaryLinks.Addon.Configuration;

namespace TemporaryLinks.Addon.Services;

public class HomeAssistantService : IHomeAssistantService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HomeAssistantService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly AddonConfiguration _config;
    private long _wsMessageId = 1;

    public HomeAssistantService(
        HttpClient httpClient,
        IOptions<AddonConfiguration> config,
        ILogger<HomeAssistantService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _config = config.Value;

        var haUrl = config.Value.HaUrl.TrimEnd('/');
        _httpClient.BaseAddress = new Uri($"{haUrl}/api/");
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.Value.HaToken);

        _logger.LogInformation("Using direct HA API access at {BaseUrl}", _httpClient.BaseAddress);

        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task<string> CreateWebhookAutomationAsync(
        string token,
        string linkName,
        string actionsJson,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        CancellationToken cancellationToken = default)
    {
        var automationId = AutomationModel.WebhookIdFor(token);

        // Parse the link's actions purely to validate them here — the automation NEVER
        // carries them. It only announces the attempt to the add-on, which runs the actions
        // itself after atomically claiming a use (E7.S1), so the allowance binds the actions
        // rather than being counted after the fact.
        try
        {
            _ = JsonSerializer.Deserialize<object>(actionsJson, _jsonOptions)
                ?? throw new InvalidOperationException("Actions JSON is null");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse actions JSON");
            throw new InvalidOperationException($"Invalid actions JSON: {ex.Message}");
        }

        var automation = AutomationModel.BuildAutomation(
            token, linkName, validFrom, validUntil, AutomationModel.AcceptsPost(_config));

        var content = new StringContent(
            JsonSerializer.Serialize(automation, _jsonOptions),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(
            $"config/automation/config/{automationId}",
            content,
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Created webhook automation {AutomationId}", automationId);
            return automationId;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"Failed to create webhook automation: {response.StatusCode} - {errorBody}");
    }

    public async Task ExecuteActionsAsync(
        string actionsJson, CancellationToken cancellationToken = default)
    {
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(actionsJson, _jsonOptions);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Invalid actions JSON: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Actions must be a JSON array.");
        }

        foreach (var action in root.EnumerateArray())
        {
            if (!action.TryGetProperty("action", out var actionEl) ||
                actionEl.GetString() is not { Length: > 0 } service ||
                !service.Contains('.'))
            {
                throw new InvalidOperationException(
                    "Each action must have an \"action\" of the form \"domain.service\".");
            }

            var dot = service.IndexOf('.');
            var domain = service[..dot];
            var name = service[(dot + 1)..];

            // Merge target (e.g. entity_id) and data into the service-call body.
            var body = new Dictionary<string, JsonElement>();
            if (action.TryGetProperty("target", out var target) &&
                target.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in target.EnumerateObject()) body[p.Name] = p.Value;
            }
            if (action.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in data.EnumerateObject()) body[p.Name] = p.Value;
            }

            var content = new StringContent(
                JsonSerializer.Serialize(body, _jsonOptions), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(
                $"services/{domain}/{name}", content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"Home Assistant refused action {service}: {response.StatusCode} - {err}");
            }
        }
    }

    public async Task<IReadOnlyList<HaServiceInfo>> GetServicesAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync("services", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));

        var result = new List<HaServiceInfo>();
        foreach (var domainEl in doc.RootElement.EnumerateArray())
        {
            var domain = domainEl.GetProperty("domain").GetString();
            if (domain == null || !domainEl.TryGetProperty("services", out var services))
                continue;
            foreach (var svc in services.EnumerateObject())
            {
                string? name = null;
                if (svc.Value.ValueKind == JsonValueKind.Object &&
                    svc.Value.TryGetProperty("name", out var nameEl))
                {
                    name = nameEl.GetString();
                }
                result.Add(new HaServiceInfo(domain, svc.Name, name));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<HaEntityInfo>> GetEntitiesAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync("states", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));

        var result = new List<HaEntityInfo>();
        foreach (var stateEl in doc.RootElement.EnumerateArray())
        {
            var entityId = stateEl.GetProperty("entity_id").GetString();
            if (entityId == null)
                continue;
            string? friendly = null;
            if (stateEl.TryGetProperty("attributes", out var attrs) &&
                attrs.ValueKind == JsonValueKind.Object &&
                attrs.TryGetProperty("friendly_name", out var fn))
            {
                friendly = fn.GetString();
            }
            result.Add(new HaEntityInfo(entityId, friendly));
        }
        return result;
    }

    public async Task<bool> DeleteWebhookAutomationAsync(
        string automationId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Attempting to delete webhook automation {AutomationId}", automationId);

        var response = await _httpClient.DeleteAsync(
            $"config/automation/config/{automationId}",
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone — the trigger is confirmed absent, nothing was removed now.
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to delete webhook automation: {response.StatusCode}");
        }

        return true;
    }

    public async Task<JsonElement?> TryGetAutomationConfigAsync(
        string automationId,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(
            $"config/automation/config/{automationId}", cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to read automation {automationId}: {response.StatusCode}");
        }

        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return doc.RootElement.Clone();
    }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetAutomationLastTriggeredAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync("states", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));

        var result = new Dictionary<string, DateTimeOffset>();
        foreach (var state in doc.RootElement.EnumerateArray())
        {
            if (state.GetProperty("entity_id").GetString() is not { } entityId ||
                !entityId.StartsWith("automation.", StringComparison.Ordinal) ||
                !state.TryGetProperty("attributes", out var attributes) ||
                attributes.ValueKind != JsonValueKind.Object ||
                !attributes.TryGetProperty("id", out var id) ||
                id.GetString() is not { Length: > 0 } automationId ||
                !attributes.TryGetProperty("last_triggered", out var lastTriggered) ||
                lastTriggered.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(
                    lastTriggered.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var fired))
            {
                continue;
            }

            result[automationId] = fired;
        }

        return result;
    }

    public async Task<CloudhookResult> CreateCloudhookAsync(
        string webhookId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating cloudhook for webhook {WebhookId}", webhookId);

        var result = await SendWebSocketCommandAsync<CloudhookResponse>(
            "cloud/cloudhook/create",
            new { webhook_id = webhookId },
            cancellationToken);

        if (result != null)
        {
            _logger.LogInformation("Created cloudhook {CloudhookId}", result.CloudhookId);
            return new CloudhookResult(result.WebhookId, result.CloudhookId, result.CloudhookUrl);
        }

        throw new InvalidOperationException($"Failed to create cloudhook for webhook {webhookId}");
    }

    public async Task DeleteCloudhookAsync(
        string webhookId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deleting cloudhook for webhook {WebhookId}", webhookId);

        var result = await SendWebSocketCommandAsync<object>(
            "cloud/cloudhook/delete",
            new { webhook_id = webhookId },
            cancellationToken);

        if (result == null)
        {
            throw new InvalidOperationException(
                $"Failed to delete cloudhook for webhook {webhookId}");
        }
    }

    public async Task<string?> GetRemoteUiUrlAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await SendWebSocketCommandAsync<CloudStatusResponse>(
                "cloud/status", new { }, cancellationToken);

            if (status?.RemoteDomain is { Length: > 0 } domain && status.RemoteEnabled != false)
            {
                var url = $"https://{domain}";
                _logger.LogInformation("Discovered public remote UI URL: {Url}", url);
                return url;
            }

            _logger.LogInformation(
                "No usable remote UI (remote_enabled={Enabled}, remote_domain={Domain})",
                status?.RemoteEnabled, status?.RemoteDomain);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Cloud status unavailable — no public URL discovered");
            return null;
        }
    }

    private async Task<T?> SendWebSocketCommandAsync<T>(
        string type,
        object data,
        CancellationToken cancellationToken) where T : class
    {
        using var ws = new ClientWebSocket();

        try
        {
            var haUrl = _config.HaUrl.Replace("http://", "ws://").Replace("https://", "wss://").TrimEnd('/');
            var wsUri = $"{haUrl}/api/websocket";


            _logger.LogInformation("Connecting to WebSocket at {Uri} for command", wsUri);
            await ws.ConnectAsync(new Uri(wsUri), cancellationToken);

            var buffer = new byte[4096];
            var messageBuilder = new StringBuilder();

            // Read auth_required message
            await ReceiveMessageAsync(ws, buffer, messageBuilder, cancellationToken);

            // Send auth
            await SendWsMessageAsync(ws, new { type = "auth", access_token = _config.HaToken }, cancellationToken);

            // Read auth response
            var authResponse = await ReceiveMessageAsync(ws, buffer, messageBuilder, cancellationToken);
            using var authDoc = JsonDocument.Parse(authResponse);
            if (authDoc.RootElement.GetProperty("type").GetString() != "auth_ok")
            {
                throw new InvalidOperationException("WebSocket authentication failed");
            }

            // Send command
            var messageId = Interlocked.Increment(ref _wsMessageId);
            var command = new Dictionary<string, object>
            {
                ["id"] = messageId,
                ["type"] = type
            };

            // Merge data properties
            var dataJson = JsonSerializer.Serialize(data, _jsonOptions);
            var dataDict = JsonSerializer.Deserialize<Dictionary<string, object>>(dataJson, _jsonOptions);
            if (dataDict != null)
            {
                foreach (var kvp in dataDict)
                {
                    command[kvp.Key] = kvp.Value;
                }
            }

            await SendWsMessageAsync(ws, command, cancellationToken);

            // Read response
            var response = await ReceiveMessageAsync(ws, buffer, messageBuilder, cancellationToken);
            using var responseDoc = JsonDocument.Parse(response);

            if (!responseDoc.RootElement.TryGetProperty("success", out var successProp) ||
                !successProp.GetBoolean())
            {
                _logger.LogError("WebSocket command failed: {Response}", response);
                return null;
            }

            if (typeof(T) == typeof(object))
            {
                return (T)(object)new { };
            }

            if (responseDoc.RootElement.TryGetProperty("result", out var resultProp))
            {
                return JsonSerializer.Deserialize<T>(resultProp.GetRawText(), _jsonOptions);
            }

            return null;
        }
        finally
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            }
        }
    }

    private async Task SendWsMessageAsync(ClientWebSocket ws, object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task<string> ReceiveMessageAsync(
        ClientWebSocket ws,
        byte[] buffer,
        StringBuilder messageBuilder,
        CancellationToken cancellationToken)
    {
        messageBuilder.Clear();

        while (true)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            var chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
            messageBuilder.Append(chunk);

            if (result.EndOfMessage)
            {
                return messageBuilder.ToString();
            }
        }
    }

    private class CloudStatusResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("remote_domain")]
        public string? RemoteDomain { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("remote_enabled")]
        public bool? RemoteEnabled { get; set; }
    }

    private class CloudhookResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("webhook_id")]
        public string WebhookId { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("cloudhook_id")]
        public string CloudhookId { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("cloudhook_url")]
        public string CloudhookUrl { get; set; } = string.Empty;
    }
}