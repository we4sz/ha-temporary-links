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

        // Check if direct HA URL and token are configured
        if (!string.IsNullOrWhiteSpace(config.Value.HaUrl) &&
            !string.IsNullOrWhiteSpace(config.Value.HaToken))
        {
            // Use direct HA API access (bypasses supervisor)
            var haUrl = config.Value.HaUrl.TrimEnd('/');
            _httpClient.BaseAddress = new Uri($"{haUrl}/api/");
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.Value.HaToken);

            _logger.LogInformation("Using direct HA API access at {BaseUrl}", _httpClient.BaseAddress);
        }
        else
        {
            // Fall back to supervisor proxy
            _httpClient.BaseAddress = new Uri(config.Value.BaseUri);

            var token = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN")
                ?? config.Value.Token
                ?? throw new InvalidOperationException("SUPERVISOR_TOKEN not available and no HA token configured");

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            _logger.LogInformation("Using supervisor proxy at {BaseUrl}", _httpClient.BaseAddress);
        }

        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task<bool> CallScriptAsync(
        string scriptEntityId,
        string? dataJson = null,
        CancellationToken cancellationToken = default)
    {
        var parts = scriptEntityId.Split('.', 2);
        if (parts.Length != 2)
        {
            _logger.LogError("Invalid script entity ID format: {EntityId}", scriptEntityId);
            return false;
        }

        var domain = parts[0];
        var service = parts[1];
        var endpoint = $"services/{domain}/{service}";

        try
        {
            _logger.LogInformation("Calling HA service: {Domain}.{Service}", domain, service);

            object? requestBody = null;
            if (!string.IsNullOrWhiteSpace(dataJson))
            {
                requestBody = JsonSerializer.Deserialize<object>(dataJson, _jsonOptions);
            }

            var content = requestBody != null
                ? new StringContent(JsonSerializer.Serialize(requestBody, _jsonOptions), Encoding.UTF8, "application/json")
                : null;

            var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully called {Domain}.{Service}", domain, service);
                return true;
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("HA API call failed: {StatusCode} - {Body}",
                response.StatusCode, errorBody);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception calling HA service {Domain}.{Service}", domain, service);
            return false;
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Home Assistant API");
            return false;
        }
    }

    public async Task<IReadOnlyList<EntityInfo>> GetEntitiesAsync(
        string? domainFilter = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Fetching entities from HA API (filter: {Filter})", domainFilter ?? "none");
            var response = await _httpClient.GetAsync("states", cancellationToken);

            _logger.LogInformation("HA API response: {StatusCode}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to get entities: {StatusCode} - {Body}", response.StatusCode, errorBody);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation("Got {Length} bytes from HA API", json.Length);

            var states = JsonSerializer.Deserialize<List<HaStateResponse>>(json, _jsonOptions);
            _logger.LogInformation("Deserialized {Count} states", states?.Count ?? 0);

            if (states == null)
                return [];

            var entities = states
                .Where(s => string.IsNullOrEmpty(domainFilter) ||
                           s.EntityId.StartsWith(domainFilter + ".", StringComparison.OrdinalIgnoreCase))
                .Select(s => new EntityInfo(
                    s.EntityId,
                    s.Attributes?.GetValueOrDefault("friendly_name")?.ToString()))
                .OrderBy(e => e.EntityId)
                .ToList();

            _logger.LogInformation("Filtered to {Count} entities", entities.Count);
            return entities;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception getting entities from HA");
            return [];
        }
    }

    public async Task<HaConfig?> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Fetching HA configuration");
            var response = await _httpClient.GetAsync("config", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to get HA config: {StatusCode} - {Body}", response.StatusCode, errorBody);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation("Configjson: {json}", json);
            var configResponse = JsonSerializer.Deserialize<HaConfigResponse>(json, _jsonOptions);

            if (configResponse == null)
            {
                _logger.LogWarning("HA config response was null after deserialization");
                return null;
            }

            var config = new HaConfig(configResponse.ExternalUrl, configResponse.InternalUrl);
            _logger.LogInformation("HA Config {configResponse}", configResponse);

            return config;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception getting HA config");
            return null;
        }
    }

    public async Task<string?> CreateWebhookAutomationAsync(
        string token,
        string linkName,
        string actionsJson,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var webhookId = $"temp_link_{token}";
            var automationId = $"temp_link_{token}";

            // Parse custom actions from JSON
            object customActions;
            try
            {
                customActions = JsonSerializer.Deserialize<object>(actionsJson, _jsonOptions)
                    ?? throw new InvalidOperationException("Actions JSON is null");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse actions JSON: {ActionsJson}", actionsJson);
                throw new InvalidOperationException($"Invalid actions JSON: {ex.Message}");
            }

            // Build actions array: event trigger first (for tracking), then custom actions
            var actionsArray = new List<object>
            {
                // First action: fire event for tracking
                new
                {
                    @event = "temp_link_triggered",
                    event_data = new
                    {
                        token,
                        link_name = linkName,
                        webhook_id = webhookId
                    }
                }
            };

            // Add custom actions
            if (customActions is System.Text.Json.JsonElement jsonElement && jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var action in jsonElement.EnumerateArray())
                {
                    actionsArray.Add(action);
                }
            }

            // Create automation config
            var automation = new
            {
                id = automationId,  // Include the ID in the payload
                alias = $"Temp Link: {linkName}",
                description = $"Webhook handler for temporary link: {linkName}. Valid from {validFrom:u} to {validUntil:u}",
                trigger = new[]
                {
                    new
                    {
                        platform = "webhook",
                        webhook_id = webhookId,
                        allowed_methods = new[] { "GET", "POST" },
                        local_only = false
                    }
                },
                action = actionsArray,  // Event + custom actions
                mode = "single"
            };

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
                _logger.LogInformation("Created webhook automation {AutomationId} for token {Token}",
                    automationId, token);
                return automationId;
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to create webhook automation: {StatusCode} - {Body}",
                response.StatusCode, errorBody);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception creating webhook automation for token {Token}", token);
            return null;
        }
    }

    public async Task<bool> DeleteWebhookAutomationAsync(
        string automationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Attempting to delete webhook automation {AutomationId}", automationId);

            // Try DELETE first (works with direct HA API access)
            var response = await _httpClient.DeleteAsync(
                $"config/automation/config/{automationId}",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully deleted webhook automation {AutomationId}", automationId);
                return true;
            }

            // 404 is ok - automation already deleted
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Webhook automation {AutomationId} not found (already deleted)", automationId);
                return true;
            }

            // If DELETE failed (e.g., 405 Method Not Allowed on supervisor proxy)
            // Fall back to disabling the automation
            _logger.LogWarning("DELETE failed with {StatusCode}, falling back to disable", response.StatusCode);

            var turnOffSuccess = await CallScriptAsync(
                "automation.turn_off",
                $"{{\"entity_id\": \"automation.{automationId}\"}}",
                cancellationToken);

            if (turnOffSuccess)
            {
                _logger.LogInformation("Successfully disabled webhook automation {AutomationId}", automationId);
                return true;
            }

            _logger.LogError("Failed to delete or disable webhook automation {AutomationId}", automationId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception deleting webhook automation {AutomationId}", automationId);
            return false;
        }
    }

    public async Task<CloudhookResult?> CreateCloudhookAsync(
        string webhookId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Creating cloudhook for webhook {WebhookId}", webhookId);

            var result = await SendWebSocketCommandAsync<CloudhookResponse>(
                "cloud/cloudhook/create",
                new { webhook_id = webhookId },
                cancellationToken);

            if (result != null)
            {
                _logger.LogInformation("Created cloudhook {CloudhookId} with URL {CloudhookUrl}",
                    result.CloudhookId, result.CloudhookUrl);
                return new CloudhookResult(result.WebhookId, result.CloudhookId, result.CloudhookUrl);
            }

            _logger.LogWarning("Failed to create cloudhook for webhook {WebhookId}", webhookId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception creating cloudhook for webhook {WebhookId}", webhookId);
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
            // Determine WebSocket URL
            string wsUri;
            string token;

            if (!string.IsNullOrWhiteSpace(_config.HaUrl))
            {
                var haUrl = _config.HaUrl.Replace("http://", "ws://").Replace("https://", "wss://").TrimEnd('/');
                wsUri = $"{haUrl}/api/websocket";
                token = _config.HaToken ?? throw new InvalidOperationException("HA token not configured");
            }
            else
            {
                wsUri = "ws://supervisor/core/websocket";
                token = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN")
                    ?? _config.Token
                    ?? throw new InvalidOperationException("No authentication token available");
            }

            _logger.LogInformation("Connecting to WebSocket at {Uri} for command", wsUri);
            await ws.ConnectAsync(new Uri(wsUri), cancellationToken);

            var buffer = new byte[4096];
            var messageBuilder = new StringBuilder();

            // Read auth_required message
            await ReceiveMessageAsync(ws, buffer, messageBuilder, cancellationToken);

            // Send auth
            await SendWsMessageAsync(ws, new { type = "auth", access_token = token }, cancellationToken);

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

    private class CloudhookResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("webhook_id")]
        public string WebhookId { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("cloudhook_id")]
        public string CloudhookId { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("cloudhook_url")]
        public string CloudhookUrl { get; set; } = string.Empty;
    }

    private class HaStateResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("entity_id")]
        public string EntityId { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("attributes")]
        public Dictionary<string, object>? Attributes { get; set; }
    }

    private class HaConfigResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("latitude")]
        public double? Latitude { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("longitude")]
        public double? Longitude { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("elevation")]
        public int? Elevation { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("unit_system")]
        public object? UnitSystem { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("location_name")]
        public string? LocationName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("time_zone")]
        public string? TimeZone { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("components")]
        public List<string>? Components { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("config_dir")]
        public string? ConfigDir { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("whitelist_external_dirs")]
        public List<string>? WhitelistExternalDirs { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("allowlist_external_dirs")]
        public List<string>? AllowlistExternalDirs { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("allowlist_external_urls")]
        public List<string>? AllowlistExternalUrls { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("version")]
        public string? Version { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("config_source")]
        public string? ConfigSource { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("safe_mode")]
        public bool? SafeMode { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("state")]
        public string? State { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("external_url")]
        public string? ExternalUrl { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("internal_url")]
        public string? InternalUrl { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("currency")]
        public string? Currency { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("country")]
        public string? Country { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("language")]
        public string? Language { get; set; }
    }
}
