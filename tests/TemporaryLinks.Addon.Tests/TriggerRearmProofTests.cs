using System.Text.Json;
using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;
using Xunit;

namespace TemporaryLinks.Addon.Tests;

/// <summary>
/// A link outlives the version that issued it. Its home-side trigger must be brought up to the
/// current enforcement model and sharing mode, and the URL it hands out must match the gesture
/// that trigger actually accepts.
/// </summary>
public class TriggerRearmProofTests
{
    /// <summary>A v1.0 trigger: it embeds the link's real actions and runs them itself on every
    /// fetch — the shape the re-arm exists to eliminate.</summary>
    private static string LegacyAutomation(string webhookId) =>
        JsonSerializer.Serialize(new
        {
            id = webhookId,
            trigger = new[]
            {
                new { platform = "webhook", webhook_id = webhookId, allowed_methods = new[] { "GET" } },
            },
            condition = new[]
            {
                new { condition = "template", value_template = "{{ true }}" },
            },
            action = new object[]
            {
                new { @event = "temp_link_triggered" },
                new { action = "lock.unlock", target = new { entity_id = "lock.front_door" } },
            },
            mode = "single",
        });

    // Proves app::E7.S7.A1 — a trigger from an older enforcement model (one that embeds and
    // runs the link's real actions) is re-armed in place to the current one, and the re-arm
    // is audited.
    [Fact]
    public async Task A_legacy_trigger_is_rearmed_to_the_current_model_and_audited()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync();
        h.Ha.StoredAutomations[link.WebhookId] = LegacyAutomation(link.WebhookId);

        var result = await h.Service.RearmTriggersAsync();

        Assert.Equal(1, result.Rearmed);
        Assert.Equal(0, result.Failed);
        Assert.Contains(link.WebhookId, h.Ha.CreatedAutomations);
        var audit = Assert.Single(await h.AuditsForAsync(link.Id), a => a.EventType == "TriggerRearmed");
        Assert.Contains("re-armed", audit.Description);

        // What now stands in the home is the current model: events only, no real actions.
        var stored = h.Ha.StoredAutomations[link.WebhookId];
        Assert.DoesNotContain("lock.unlock", stored);
        Assert.Contains(AutomationModel.BlockedEvent, stored);
    }

    // Proves app::E7.S7.A1 — a trigger that already matches is left completely alone: no
    // re-arming, no audit noise on every boot.
    [Fact]
    public async Task A_current_trigger_is_left_alone()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(triggerAcceptsPost: false, armInFakeHome: true);

        var result = await h.Service.RearmTriggersAsync();

        Assert.Equal(1, result.Checked);
        Assert.Equal(0, result.Rearmed);
        Assert.Empty(h.Ha.CreatedAutomations);
        Assert.Empty(await h.AuditsForAsync(link.Id));
    }

    // Proves app::E7.S7.A1 — a link whose trigger the home no longer has at all is re-armed too.
    [Fact]
    public async Task A_missing_trigger_is_rearmed()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(triggerAcceptsPost: false);
        Assert.Empty(h.Ha.StoredAutomations);

        var result = await h.Service.RearmTriggersAsync();

        Assert.Equal(1, result.Rearmed);
        Assert.Contains(link.WebhookId, h.Ha.CreatedAutomations);
        Assert.Single(await h.AuditsForAsync(link.Id), a => a.EventType == "TriggerRearmed");
    }

    // Proves app::E7.S7.A1 — a sharing-mode change (a confirm page is now configured) re-arms
    // the accepted gesture of every existing link's trigger.
    [Fact]
    public async Task A_sharing_mode_change_rearms_the_accepted_gesture()
    {
        using var h = new LinkServiceHarness(publicUrl: "https://example.ui.nabu.casa");
        // Armed as one-tap GET before the confirm page existed.
        var link = await h.SeedLinkAsync(triggerAcceptsPost: false);
        h.Ha.StoredAutomations[link.WebhookId] = JsonSerializer.Serialize(
            AutomationModel.BuildAutomation(
                link.Token, link.Name, link.ValidFrom, link.ValidUntil, acceptsPost: false));

        var result = await h.Service.RearmTriggersAsync();

        Assert.Equal(1, result.Rearmed);
        Assert.True(link.TriggerAcceptsPost);
        using var stored = JsonDocument.Parse(h.Ha.StoredAutomations[link.WebhookId]);
        Assert.Equal("POST", stored.RootElement.GetProperty("trigger")[0]
            .GetProperty("allowed_methods")[0].GetString());
    }

    // Proves app::E7.S7.A1 — a dead link is not re-armed: its trigger is supposed to be gone.
    [Fact]
    public async Task Dead_links_are_not_rearmed()
    {
        using var h = new LinkServiceHarness();
        await h.SeedLinkAsync(status: LinkStatus.Revoked);
        await h.SeedLinkAsync(status: LinkStatus.Used);

        var result = await h.Service.RearmTriggersAsync();

        Assert.Equal(0, result.Checked);
        Assert.Empty(h.Ha.CreatedAutomations);
    }

    // Proves app::E7.S7.A1 — one link the home refuses does not stop the others, and the pass
    // reports the failure so it is retried.
    [Fact]
    public async Task A_failure_on_one_link_is_reported_and_does_not_stop_the_pass()
    {
        using var h = new LinkServiceHarness();
        await h.SeedLinkAsync();
        h.Ha.ThrowOnCreateAutomation = new InvalidOperationException("HA unreachable");

        var result = await h.Service.RearmTriggersAsync();

        Assert.Equal(1, result.Checked);
        Assert.Equal(0, result.Rearmed);
        Assert.Equal(1, result.Failed);
    }

    // Proves app::E7.S7.A2 — the share URL follows the ARMED gesture, not the current
    // configuration: a link armed for one tap keeps handing out the URL its trigger accepts,
    // even after a confirm page is configured (which would otherwise 405 it).
    [Fact]
    public async Task A_get_armed_link_keeps_handing_out_the_url_its_trigger_accepts()
    {
        using var h = new LinkServiceHarness(publicUrl: "https://example.ui.nabu.casa");
        var link = await h.SeedLinkAsync(triggerAcceptsPost: false);

        Assert.Equal(link.CloudhookUrl, h.Service.GetShareUrl(link));
    }

    // Proves app::E7.S7.A2 — and the converse: a POST-armed link is shared through the confirm
    // page that makes that gesture.
    [Fact]
    public async Task A_post_armed_link_is_shared_through_the_confirm_page()
    {
        using var h = new LinkServiceHarness(publicUrl: "https://example.ui.nabu.casa");
        var link = await h.SeedLinkAsync(triggerAcceptsPost: true);

        var url = h.Service.GetShareUrl(link);

        Assert.StartsWith("https://example.ui.nabu.casa/local/", url);
        Assert.Contains(Uri.EscapeDataString(link.CloudhookUrl), url);
    }

    // Proves app::E7.S7.A2 — creation records the gesture it armed, so the URL and the trigger
    // agree from the very first share.
    [Fact]
    public async Task Creation_records_the_gesture_it_armed()
    {
        using var h = new LinkServiceHarness(publicUrl: "https://example.ui.nabu.casa");
        var now = DateTimeOffset.UtcNow;

        var link = await h.Service.CreateLinkAsync(
            "Gate", now, now.AddHours(1), null, null, "test", "[]");

        Assert.True(link.TriggerAcceptsPost);
        Assert.StartsWith("https://example.ui.nabu.casa/local/", h.Service.GetShareUrl(link));
    }
}
