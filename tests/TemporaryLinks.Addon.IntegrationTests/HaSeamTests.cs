using System.Net;
using System.Text.Json;
using TemporaryLinks.Addon.Services;

namespace TemporaryLinks.Addon.IntegrationTests;

/// <summary>
/// Proves the add-on ↔ Home Assistant seam against a REAL HA instance (see
/// tests/integration/run-ha-tests.sh): that HA accepts the generated automation,
/// that the home itself enforces the validity window, that the confirm-page mode is
/// POST-only, and that the action-picker feed and service execution are real.
/// Unit tests only inspect the payloads we SEND; these prove how HA answers.
/// </summary>
[Collection("ha")]
public sealed class HaSeamTests(HaFixture ha)
{
    private const string TestActions =
        """[{"action":"persistent_notification.create","data":{"message":"integration test"}}]""";

    private static string NewToken() => $"ittest{Guid.NewGuid():N}";

    private static JsonElement PluralOrSingular(JsonElement element, string plural, string singular)
        => element.TryGetProperty(plural, out var value) ? value : element.GetProperty(singular);

    [SkippableFact]
    public async Task Action_picker_feed_lists_the_homes_real_services()
    {
        ha.SkipUnlessConfigured();
        var services = await ha.CreateService().GetServicesAsync();

        Assert.Contains(services, s => s.Domain == "homeassistant" && s.Service == "turn_on");
        Assert.Contains(services, s => s.Domain == "persistent_notification" && s.Service == "create");
    }

    [SkippableFact]
    public async Task Action_picker_feed_lists_the_homes_real_entities()
    {
        ha.SkipUnlessConfigured();
        var entities = await ha.CreateService().GetEntitiesAsync();

        Assert.Contains(entities, e => e.EntityId == "sun.sun");
        Assert.Contains(entities, e => e.EntityId == "zone.home");
    }

    [SkippableFact]
    public async Task Real_ha_accepts_and_loads_the_generated_automation()
    {
        ha.SkipUnlessConfigured();
        var service = ha.CreateService();
        var token = NewToken();

        var automationId = await service.CreateWebhookAutomationAsync(
            token, "Integration Test Link", TestActions,
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        try
        {
            var stored = await ha.TryGetAutomationConfigAsync(automationId);
            Assert.NotNull(stored);
            // HA normalizes automation config to plural keys (triggers/conditions);
            // older versions echo back the singular ones we send.
            var trigger = PluralOrSingular(stored.Value, "triggers", "trigger")[0];
            Assert.Equal($"temp_link_{token}", trigger.GetProperty("webhook_id").GetString());
            Assert.Equal("template", PluralOrSingular(stored.Value, "conditions", "condition")[0]
                .GetProperty("condition").GetString());

            // Stored is not enough — HA must actually load it (a template syntax error
            // would surface here, not at the config POST).
            await ha.WaitUntilAutomationLoadedAsync(automationId, TimeSpan.FromSeconds(15));
        }
        finally
        {
            await service.DeleteWebhookAutomationAsync(automationId);
        }
    }

    [SkippableFact]
    public async Task One_tap_link_inside_window_fires_the_tracking_event()
    {
        ha.SkipUnlessConfigured();
        var service = ha.CreateService(); // no confirm page -> one-tap GET links
        var token = NewToken();

        var automationId = await service.CreateWebhookAutomationAsync(
            token, "Window Open Link", TestActions,
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        try
        {
            await ha.WaitUntilAutomationLoadedAsync(automationId, TimeSpan.FromSeconds(15));
            await using var events = await ha.SubscribeAsync("temp_link_triggered");

            var response = await ha.GetWebhookAsync($"temp_link_{token}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var evt = await events.WaitForMatchAsync(
                e => e.GetProperty("token").GetString() == token, TimeSpan.FromSeconds(10));
            Assert.NotNull(evt);
            Assert.Equal($"temp_link_{token}", evt.Value.GetProperty("webhook_id").GetString());
        }
        finally
        {
            await service.DeleteWebhookAutomationAsync(automationId);
        }
    }

    [SkippableFact]
    public async Task Outside_the_window_the_home_itself_blocks_the_trigger()
    {
        ha.SkipUnlessConfigured();
        var service = ha.CreateService();
        var token = NewToken();

        var automationId = await service.CreateWebhookAutomationAsync(
            token, "Window Closed Link", TestActions,
            DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1));
        try
        {
            await ha.WaitUntilAutomationLoadedAsync(automationId, TimeSpan.FromSeconds(15));
            await using var events = await ha.SubscribeAsync("temp_link_triggered");

            await ha.GetWebhookAsync($"temp_link_{token}");

            var evt = await events.WaitForMatchAsync(
                e => e.GetProperty("token").GetString() == token, TimeSpan.FromSeconds(4));
            Assert.Null(evt); // the condition refused it — enforced by the home, not by us
        }
        finally
        {
            await service.DeleteWebhookAutomationAsync(automationId);
        }
    }

    [SkippableFact]
    public async Task Confirm_page_links_accept_only_post_so_preview_bots_cannot_fire_them()
    {
        ha.SkipUnlessConfigured();
        var service = ha.CreateService(c => c.PublicUrl = "https://example.ui.nabu.casa");
        var token = NewToken();

        var automationId = await service.CreateWebhookAutomationAsync(
            token, "Bot Immune Link", TestActions,
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        try
        {
            await ha.WaitUntilAutomationLoadedAsync(automationId, TimeSpan.FromSeconds(15));
            await using var events = await ha.SubscribeAsync("temp_link_triggered");

            // A preview bot's GET must be rejected outright...
            var get = await ha.GetWebhookAsync($"temp_link_{token}");
            Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);

            // ...while the confirm page's explicit POST fires the trigger.
            var post = await ha.PostWebhookAsync($"temp_link_{token}");
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
            var evt = await events.WaitForMatchAsync(
                e => e.GetProperty("token").GetString() == token, TimeSpan.FromSeconds(10));
            Assert.NotNull(evt);
        }
        finally
        {
            await service.DeleteWebhookAutomationAsync(automationId);
        }
    }

    [SkippableFact]
    public async Task Execute_actions_really_calls_services_in_the_home()
    {
        ha.SkipUnlessConfigured();
        var marker = Guid.NewGuid().ToString("N");
        await using var events = await ha.SubscribeAsync("call_service");

        await ha.CreateService().ExecuteActionsAsync(JsonSerializer.Serialize(new[]
        {
            new { action = "persistent_notification.create", data = new { message = marker } },
        }));

        var evt = await events.WaitForMatchAsync(
            e => e.GetProperty("domain").GetString() == "persistent_notification" &&
                 e.GetProperty("service").GetString() == "create" &&
                 e.GetProperty("service_data").TryGetProperty("message", out var m) &&
                 m.GetString() == marker,
            TimeSpan.FromSeconds(10));
        Assert.NotNull(evt);
    }
}
