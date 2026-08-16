using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;
using Xunit;

namespace TemporaryLinks.Addon.Tests;

/// <summary>A dead link's trigger must not outlive it: every removal goes through one path
/// that audits its failure distinctly and leaves the link marked as still carrying a trigger,
/// which the sweep keeps retrying until the home confirms it is gone.</summary>
public class TriggerRemovalProofTests
{
    // Proves app::E1.S4.A5 — the sweep retries a removal the home refused earlier, for every
    // dead link that still carries a trigger, and audits the removal when it finally lands.
    [Fact]
    public async Task Sweep_retries_a_standing_trigger_on_a_dead_link_until_it_lands()
    {
        using var h = new LinkServiceHarness();
        var now = DateTimeOffset.UtcNow;
        h.Ha.ThrowOnDelete = new InvalidOperationException("HA unreachable");
        var link = await h.SeedLinkAsync(validFrom: now.AddHours(-2), validUntil: now.AddHours(-1));
        var webhookId = link.WebhookId;

        // First pass: the link dies, but the home refuses to give up the trigger.
        await h.Service.ExpireOldLinksAsync();
        Assert.Equal(LinkStatus.Expired, link.Status);
        Assert.Empty(h.Ha.DeletedAutomations);
        Assert.Equal(webhookId, link.WebhookId); // still standing — marked for retry
        Assert.Single(await h.AuditsForAsync(link.Id), a => a.EventType == "WebhookDeleteFailed");

        // Home still down: the retry runs but does not re-audit a failure already on the
        // record — an outage must not fill the audit trail one entry per link per sweep.
        await h.Service.ExpireOldLinksAsync();
        Assert.Single(await h.AuditsForAsync(link.Id), a => a.EventType == "WebhookDeleteFailed");

        // Home back: the sweep retries the dead link and the removal lands.
        h.Ha.ThrowOnDelete = null;
        await h.Service.ExpireOldLinksAsync();

        Assert.Contains(webhookId, h.Ha.DeletedAutomations);
        var audits = await h.AuditsForAsync(link.Id);
        Assert.Single(audits, a => a.EventType == "WebhookDeleted" && a.Success);
        Assert.Equal(string.Empty, link.WebhookId);

        // Third pass: nothing left to retry — no repeated attempts, no audit noise.
        await h.Service.ExpireOldLinksAsync();
        Assert.Single(h.Ha.DeletedAutomations);
        Assert.Single(await h.AuditsForAsync(link.Id), a => a.EventType == "WebhookDeleted");
    }

    // Proves app::E1.S4.A5 for the other two deaths — a revoked link whose removal failed is
    // retried by the sweep just the same.
    [Fact]
    public async Task Sweep_retries_a_standing_trigger_on_a_revoked_link()
    {
        using var h = new LinkServiceHarness();
        h.Ha.ThrowOnDelete = new InvalidOperationException("HA unreachable");
        var link = await h.SeedLinkAsync();
        var webhookId = link.WebhookId;

        await h.Service.RevokeLinkAsync(link.Token);
        Assert.Equal(LinkStatus.Revoked, link.Status);
        Assert.Empty(h.Ha.DeletedAutomations);

        h.Ha.ThrowOnDelete = null;
        await h.Service.ExpireOldLinksAsync();

        Assert.Contains(webhookId, h.Ha.DeletedAutomations);
        Assert.Equal(string.Empty, link.WebhookId);
    }

    // Proves app::E2.S2.A2 / app::E7.S1.A3 tail — a link whose allowance was edited down to
    // its recorded uses is exhausted the moment it is presented: the claim refuses it, and the
    // link is retired and its trigger taken out of the home, not left standing.
    [Fact]
    public async Task An_exhausted_but_still_active_link_is_retired_on_the_next_attempt()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(maxUses: 2, usageCount: 2, status: LinkStatus.Active);
        var webhookId = link.WebhookId;

        var result = await h.Service.ExecuteLinkAsync(link.Token, "webhook", "test");

        Assert.Equal(LinkExecutionStatus.AlreadyUsed, result.Status);
        Assert.Equal(LinkStatus.Used, link.Status);
        Assert.Equal(2, link.UsageCount);
        Assert.Equal(0, h.Ha.ExecutedActionsCount);
        Assert.Contains(webhookId, h.Ha.DeletedAutomations);
        var audits = await h.AuditsForAsync(link.Id);
        Assert.Single(audits, a => a.EventType == "ExecutionAttempt" && !a.Success);
        Assert.Single(audits, a => a.EventType == "WebhookDeleted");
    }

    // Proves app::E1.S4.A4 — the sweep audits a removal failure, still expires the link, and
    // carries on with the remaining links (the failure is not the link's verdict).
    [Fact]
    public async Task Sweep_audits_the_failure_expires_anyway_and_continues()
    {
        using var h = new LinkServiceHarness();
        h.Ha.ThrowOnDelete = new InvalidOperationException("HA down");
        var now = DateTimeOffset.UtcNow;
        var first = await h.SeedLinkAsync(validFrom: now.AddHours(-2), validUntil: now.AddHours(-1));
        var second = await h.SeedLinkAsync(validFrom: now.AddHours(-2), validUntil: now.AddHours(-1));

        await h.Service.ExpireOldLinksAsync();

        Assert.Equal(LinkStatus.Expired, first.Status);
        Assert.Equal(LinkStatus.Expired, second.Status);
        Assert.Single(await h.AuditsForAsync(first.Id), a => a.EventType == "WebhookDeleteFailed");
        Assert.Single(await h.AuditsForAsync(second.Id), a => a.EventType == "WebhookDeleteFailed");
    }
}
