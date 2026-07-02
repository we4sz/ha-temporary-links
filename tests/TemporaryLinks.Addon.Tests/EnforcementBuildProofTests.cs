using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TemporaryLinks.Addon.Configuration;
using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;
using Xunit;

namespace TemporaryLinks.Addon.Tests;

public class EnforcementBuildProofTests
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

    // Proves app::E7.S1.A1 / E7.S1.A3 (home side) — the automation carries ONLY the tracking
    // event, never the link's real actions, so the home cannot run them on its own (e.g. while
    // the add-on is offline). Enforcement binds the actions to the add-on's decision.
    [Fact]
    public async Task Automation_never_embeds_the_links_real_actions()
    {
        var handler = new CapturingHandler();
        var ha = NewHaService(handler);
        var now = DateTimeOffset.UtcNow;

        await ha.CreateWebhookAutomationAsync(
            "tok123", "Gate",
            "[{\"action\":\"lock.unlock\",\"target\":{\"entity_id\":\"lock.front_door\"}}]",
            now, now.AddHours(1));

        var post = Assert.Single(handler.Requests,
            r => r.Method == HttpMethod.Post && r.Path.Contains("config/automation/config/"));
        using var config = JsonDocument.Parse(post.Body!);
        var actions = config.RootElement.GetProperty("action");
        Assert.Equal(1, actions.GetArrayLength());
        Assert.Equal("temp_link_triggered", actions[0].GetProperty("event").GetString());
        Assert.DoesNotContain("lock.unlock", post.Body);
    }

    // Proves app::E7.S1.A1 (add-on side) — the add-on runs the link's actions itself, as
    // Home Assistant service calls, only when asked.
    [Fact]
    public async Task Addon_runs_actions_as_service_calls()
    {
        var handler = new CapturingHandler();
        var ha = NewHaService(handler);

        await ha.ExecuteActionsAsync(
            "[{\"action\":\"lock.unlock\",\"target\":{\"entity_id\":\"lock.front_door\"}}]");

        var call = Assert.Single(handler.Requests,
            r => r.Method == HttpMethod.Post && r.Path.EndsWith("/api/services/lock/unlock"));
        Assert.Contains("lock.front_door", call.Body);
    }

    // Proves app::E7.S3.A1 — the atomic claim, not just the up-front status check, is what
    // guards the allowance: a link whose count is already at its allowance (but still marked
    // Active, e.g. a stale/raced handler) is refused by the conditional UPDATE and its actions
    // never run.
    [Fact]
    public async Task Atomic_claim_refuses_a_use_when_the_allowance_is_already_taken()
    {
        using var h = new LinkServiceHarness();
        // Count already at the allowance while still Active — this reaches the atomic claim
        // (the Used fast-path does not apply), so it exercises the claim guard directly.
        var link = await h.SeedLinkAsync(maxUses: 1, usageCount: 1, status: LinkStatus.Active);

        var result = await h.Service.ExecuteLinkAsync(link.Token, null, null);

        Assert.Equal(LinkExecutionStatus.AlreadyUsed, result.Status);
        Assert.Equal(0, h.Ha.ExecutedActionsCount);
        Assert.Equal(1, link.UsageCount); // never pushed past the allowance
    }

    // Proves app::E7.S1.A2 — repeated triggers on a single-use link never over-consume it:
    // exactly one gets through, the actions run exactly once, and the count never exceeds the
    // allowance.
    [Fact]
    public async Task Repeated_triggers_never_over_consume_a_single_use_link()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(maxUses: 1);

        var results = new List<LinkExecutionStatus>();
        for (var i = 0; i < 5; i++)
        {
            results.Add((await h.Service.ExecuteLinkAsync(link.Token, "webhook", "test")).Status);
        }

        Assert.Equal(1, results.Count(s => s == LinkExecutionStatus.Success));
        Assert.Equal(1, h.Ha.ExecutedActionsCount);
        var reloaded = await h.Service.GetLinkByIdAsync(link.Id);
        Assert.Equal(1, reloaded!.UsageCount);
        Assert.Equal(LinkStatus.Used, reloaded.Status);
    }

    // Proves app::E7.S1 happy path — a valid use runs the actions and counts once.
    [Fact]
    public async Task Valid_use_runs_actions_and_counts_once()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(maxUses: 2);

        var result = await h.Service.ExecuteLinkAsync(link.Token, null, null);

        Assert.Equal(LinkExecutionStatus.Success, result.Status);
        Assert.Equal(1, h.Ha.ExecutedActionsCount);
        Assert.Equal(1, link.UsageCount);
    }

    // A failing action still consumes the use (so it can't be retried to bypass the limit).
    [Fact]
    public async Task Failed_action_still_consumes_the_use()
    {
        using var h = new LinkServiceHarness();
        h.Ha.ThrowOnExecuteActions = new InvalidOperationException("HA refused");
        var link = await h.SeedLinkAsync(maxUses: 1);

        var result = await h.Service.ExecuteLinkAsync(link.Token, null, null);

        Assert.Equal(LinkExecutionStatus.Error, result.Status);
        Assert.Equal(1, link.UsageCount);
        var audits = await h.AuditsForAsync(link.Id);
        Assert.Contains(audits, a => a.EventType == "ExecutionException");
    }
}
