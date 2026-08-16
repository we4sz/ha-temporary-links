using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TemporaryLinks.Addon.Configuration;
using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;
using Xunit;

namespace TemporaryLinks.Addon.Tests;

/// <summary>
/// The home decides in/out of window, but it no longer swallows the attempt: out-of-window
/// presses reach the add-on as a refusal it can audit, and refusing is all the add-on does
/// with them.
/// </summary>
public class AuditableRefusalProofTests
{
    private static HomeAssistantService NewHaService(CapturingHandler handler) =>
        new(
            new HttpClient(handler),
            Options.Create(new AddonConfiguration
            {
                HaUrl = "http://ha.test:8123",
                HaToken = "test-token",
            }),
            NullLogger<HomeAssistantService>.Instance);

    // Proves app::E2.S2.A4 / app::E2.S2.A5 (home side) — the trigger reports EVERY attempt:
    // in-window it announces a use, out-of-window it announces a refusal. Nothing but events,
    // and no top-level condition that would silently drop the out-of-window attempt.
    [Fact]
    public async Task Trigger_reports_out_of_window_attempts_instead_of_swallowing_them()
    {
        var handler = new CapturingHandler();
        var ha = NewHaService(handler);
        var now = DateTimeOffset.UtcNow;

        await ha.CreateWebhookAutomationAsync("tok123", "Gate", "[]", now, now.AddHours(1));

        var post = Assert.Single(handler.Requests,
            r => r.Method == HttpMethod.Post && r.Path.Contains("config/automation/config/"));
        using var config = JsonDocument.Parse(post.Body!);

        // No top-level condition: the automation always runs and always reports.
        Assert.False(config.RootElement.TryGetProperty("condition", out _));

        var step = config.RootElement.GetProperty("action")[0];
        var inWindow = step.GetProperty("choose")[0];
        Assert.Equal("temp_link_triggered",
            inWindow.GetProperty("sequence")[0].GetProperty("event").GetString());
        Assert.Contains("now()",
            inWindow.GetProperty("conditions")[0].GetProperty("value_template").GetString());

        var outOfWindow = step.GetProperty("default")[0];
        Assert.Equal("temp_link_blocked", outOfWindow.GetProperty("event").GetString());
        Assert.Equal("tok123", outOfWindow.GetProperty("event_data").GetProperty("token").GetString());
    }

    // Proves app::E2.S2.A5 — a refusal the home announces for a link before its window is
    // audited as a failure, and the link stays active for later use.
    [Fact]
    public async Task Refused_attempt_before_the_window_is_audited_and_leaves_the_link_active()
    {
        using var h = new LinkServiceHarness();
        var now = DateTimeOffset.UtcNow;
        var link = await h.SeedLinkAsync(validFrom: now.AddHours(1), validUntil: now.AddHours(2));

        var result = await h.Service.RecordBlockedTriggerAsync(link.Token, "webhook", "test");

        Assert.Equal(LinkExecutionStatus.NotYetValid, result.Status);
        Assert.Equal(LinkStatus.Active, link.Status);
        Assert.Equal(0, link.UsageCount);
        Assert.Equal(0, h.Ha.ExecutedActionsCount);
        var audit = Assert.Single(await h.AuditsForAsync(link.Id));
        Assert.False(audit.Success);
    }

    // Proves app::E2.S2.A4 — a refusal the home announces for a link past its window end is
    // audited as a failure, and the link is marked expired and cleaned up.
    [Fact]
    public async Task Refused_attempt_after_the_window_is_audited_and_expires_the_link()
    {
        using var h = new LinkServiceHarness();
        var now = DateTimeOffset.UtcNow;
        var link = await h.SeedLinkAsync(validFrom: now.AddHours(-3), validUntil: now.AddHours(-1));
        var webhookId = link.WebhookId;

        var result = await h.Service.RecordBlockedTriggerAsync(link.Token, "webhook", "test");

        Assert.Equal(LinkExecutionStatus.Expired, result.Status);
        Assert.Equal(LinkStatus.Expired, link.Status);
        Assert.Equal(0, h.Ha.ExecutedActionsCount);
        var audits = await h.AuditsForAsync(link.Id);
        Assert.Single(audits, a => a.EventType == "ExecutionAttempt" && !a.Success);
        Assert.Contains(webhookId, h.Ha.DeletedAutomations);
    }

    // Proves app::E2.S1.A3 — the home's refusal is final. Even when the add-on's own clock
    // says the link is comfortably inside its window (a clock-skew disagreement), a trigger
    // the home refused claims no use and runs no actions: nothing ran in the home, so nothing
    // is owed — it is recorded as a refusal, not as a use.
    [Fact]
    public async Task A_refusal_never_claims_a_use_even_when_the_addon_clock_disagrees()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(maxUses: 3); // wide open by the add-on's clock

        var result = await h.Service.RecordBlockedTriggerAsync(link.Token, "webhook", "test");

        Assert.Equal(LinkExecutionStatus.RefusedByHome, result.Status);
        Assert.Equal(0, link.UsageCount);
        Assert.Equal(LinkStatus.Active, link.Status);
        Assert.Equal(0, h.Ha.ExecutedActionsCount);
        var audit = Assert.Single(await h.AuditsForAsync(link.Id));
        Assert.Equal("ExecutionAttempt", audit.EventType);
        Assert.False(audit.Success);
        Assert.Contains("refused by the home", audit.Description);
    }

    // Proves app::E2.S2.A1 — a refusal for a token no link owns changes nothing.
    [Fact]
    public async Task Refused_attempt_for_an_unknown_token_changes_nothing()
    {
        using var h = new LinkServiceHarness();

        var result = await h.Service.RecordBlockedTriggerAsync("no-such-token", null, null);

        Assert.Equal(LinkExecutionStatus.NotFound, result.Status);
        Assert.Empty(h.Db.LinkUsageAudits);
    }

    // Proves app::E2.S2.A3 — a refusal for a revoked link is audited as a failure.
    [Fact]
    public async Task Refused_attempt_on_a_revoked_link_is_audited()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(status: LinkStatus.Revoked);

        var result = await h.Service.RecordBlockedTriggerAsync(link.Token, null, null);

        Assert.Equal(LinkExecutionStatus.Revoked, result.Status);
        var audit = Assert.Single(await h.AuditsForAsync(link.Id));
        Assert.False(audit.Success);
    }
}
