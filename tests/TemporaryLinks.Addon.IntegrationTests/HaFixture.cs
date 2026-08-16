using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TemporaryLinks.Addon.Configuration;
using TemporaryLinks.Addon.Services;

namespace TemporaryLinks.Addon.IntegrationTests;

/// <summary>
/// Connects the suite to a real Home Assistant instance named by HA_TEST_URL.
/// A fresh (never onboarded) instance is bootstrapped through the onboarding API —
/// no pre-seeded .storage fixtures that could go stale across HA versions. An already
/// onboarded instance can be used by also setting HA_TEST_TOKEN.
/// When HA_TEST_URL is unset every test in the suite skips.
/// </summary>
public sealed class HaFixture : IAsyncLifetime
{
    public string? Url { get; private set; }
    public string Token { get; private set; } = "";

    private readonly HttpClient _http = new();

    public async Task InitializeAsync()
    {
        var url = Environment.GetEnvironmentVariable("HA_TEST_URL")?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url))
        {
            return; // not configured — tests will skip
        }

        Url = url;
        _http.BaseAddress = new Uri(url + "/");

        var token = Environment.GetEnvironmentVariable("HA_TEST_TOKEN");
        Token = string.IsNullOrWhiteSpace(token)
            ? await OnboardAsync()
            : token;

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Token);
    }

    public Task DisposeAsync()
    {
        _http.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Drives a fresh HA instance through onboarding and returns an access token.</summary>
    private async Task<string> OnboardAsync()
    {
        var clientId = Url + "/";

        var authCode = await PostJsonAsync("api/onboarding/users", new
        {
            client_id = clientId,
            name = "Integration Tests",
            username = "ittest",
            password = "integration-tests-only",
            language = "en",
        }, "auth_code");

        // Exchange the onboarding auth code for an access token (OAuth code flow).
        var tokenResponse = await _http.PostAsync("auth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = authCode,
                ["client_id"] = clientId,
            }));
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync();
        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"auth/token failed: {tokenResponse.StatusCode} {tokenBody}");
        }
        var accessToken = JsonDocument.Parse(tokenBody).RootElement
            .GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("auth/token returned no access_token");

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

        // Finish the remaining onboarding steps so the API behaves like a real home.
        await PostJsonAsync("api/onboarding/core_config", new { }, expectKey: null);
        await PostJsonAsync("api/onboarding/analytics", new { }, expectKey: null);
        await PostJsonAsync("api/onboarding/integration", new
        {
            client_id = clientId,
            redirect_uri = clientId + "?auth_callback=1",
        }, expectKey: null);

        return accessToken;
    }

    private async Task<string> PostJsonAsync(string path, object payload, string? expectKey)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(path, content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{path} failed: {response.StatusCode} {body}");
        }
        if (expectKey is null)
        {
            return body;
        }
        return JsonDocument.Parse(body).RootElement.GetProperty(expectKey).GetString()
            ?? throw new InvalidOperationException($"{path} returned no {expectKey}");
    }

    /// <summary>Skips the calling test when no HA instance is configured.</summary>
    public void SkipUnlessConfigured()
        => Skip.If(Url is null, "Set HA_TEST_URL (see tests/integration/run-ha-tests.sh) to run HA integration tests.");

    /// <summary>The production service under test, pointed at the real instance.</summary>
    public HomeAssistantService CreateService(Action<AddonConfiguration>? configure = null)
    {
        var config = new AddonConfiguration { HaUrl = Url!, HaToken = Token };
        configure?.Invoke(config);
        return new HomeAssistantService(
            new HttpClient(), Options.Create(config), NullLogger<HomeAssistantService>.Instance);
    }

    public async Task<HttpResponseMessage> PostWebhookAsync(string webhookId)
        => await _http.PostAsync($"api/webhook/{webhookId}", new StringContent(""));

    public async Task<HttpResponseMessage> GetWebhookAsync(string webhookId)
        => await _http.GetAsync($"api/webhook/{webhookId}");

    public async Task<JsonElement?> TryGetAutomationConfigAsync(string automationId)
    {
        var response = await _http.GetAsync($"api/config/automation/config/{automationId}");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>Polls until the automation is not only stored but LOADED — its entity exists —
    /// so a webhook fired right after creation actually has a registered trigger.</summary>
    public async Task WaitUntilAutomationLoadedAsync(string automationId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await _http.GetAsync("api/states");
            if (response.IsSuccessStatusCode)
            {
                var states = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
                foreach (var state in states.EnumerateArray())
                {
                    if (state.GetProperty("entity_id").GetString()?.StartsWith("automation.") == true &&
                        state.TryGetProperty("attributes", out var attrs) &&
                        attrs.TryGetProperty("id", out var id) &&
                        id.GetString() == automationId)
                    {
                        return;
                    }
                }
            }
            await Task.Delay(250);
        }
        throw new TimeoutException($"Automation {automationId} was stored but never loaded as an entity.");
    }

    public async Task<HaEventSubscription> SubscribeAsync(string eventType)
        => await HaEventSubscription.OpenAsync(Url!, Token, eventType);
}

[CollectionDefinition("ha")]
public sealed class HaCollection : ICollectionFixture<HaFixture>;
