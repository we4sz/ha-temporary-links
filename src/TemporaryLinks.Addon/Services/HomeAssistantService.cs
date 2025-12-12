using System.Net.Http.Headers;
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

    public HomeAssistantService(
        HttpClient httpClient,
        IOptions<AddonConfiguration> config,
        ILogger<HomeAssistantService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(config.Value.BaseUri);

        var token = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN")
            ?? config.Value.Token
            ?? throw new InvalidOperationException("SUPERVISOR_TOKEN not available");

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
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

    public async Task<string?> CreateWebhookAutomationAsync(
        string token,
        string linkName,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var webhookId = $"temp_link_{token}";
            var automationId = $"temp_link_{token}";

            // Create automation config
            var automation = new
            {
                alias = $"Temp Link: {linkName}",
                description = $"Webhook handler for temporary link: {linkName}",
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
                condition = new[]
                {
                    new
                    {
                        condition = "time",
                        after = validFrom.ToString("HH:mm:ss"),
                        before = validUntil.ToString("HH:mm:ss")
                    }
                },
                action = new[]
                {
                    new
                    {
                        service = "shell_command.temp_link_callback",
                        data = new { token = token }
                    }
                },
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
            var response = await _httpClient.DeleteAsync(
                $"config/automation/config/{automationId}",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Deleted webhook automation {AutomationId}", automationId);
                return true;
            }

            // 404 is ok - automation might already be deleted
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Webhook automation {AutomationId} not found (already deleted?)", automationId);
                return true;
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to delete webhook automation: {StatusCode} - {Body}",
                response.StatusCode, errorBody);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception deleting webhook automation {AutomationId}", automationId);
            return false;
        }
    }

    private class HaStateResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("entity_id")]
        public string EntityId { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("attributes")]
        public Dictionary<string, object>? Attributes { get; set; }
    }
}
