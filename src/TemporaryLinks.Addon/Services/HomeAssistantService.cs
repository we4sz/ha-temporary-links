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
}
