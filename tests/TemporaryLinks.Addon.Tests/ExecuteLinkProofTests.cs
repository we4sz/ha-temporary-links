using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;
using Xunit;

namespace TemporaryLinks.Addon.Tests;

public class ExecuteLinkProofTests
{
    // Proves app::E2.S2.A1 — unknown token refused, no state change.
    [Fact]
    public async Task Unknown_token_is_refused_as_not_found()
    {
        using var h = new LinkServiceHarness();

        var result = await h.Service.ExecuteLinkAsync("no-such-token", null, null);

        Assert.Equal(LinkExecutionStatus.NotFound, result.Status);
        Assert.Empty(h.Db.LinkUsageAudits);
    }

    // Proves app::E2.S2.A2 — exhausted link refused as already-used, audited as failure.
    [Fact]
    public async Task Exhausted_link_is_refused_as_already_used()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(maxUses: 1, usageCount: 1);

        var result = await h.Service.ExecuteLinkAsync(link.Token, null, null);

        Assert.Equal(LinkExecutionStatus.AlreadyUsed, result.Status);
        Assert.Equal(1, link.UsageCount);
        var audit = Assert.Single(await h.AuditsForAsync(link.Id));
        Assert.Equal("ExecutionAttempt", audit.EventType);
        Assert.False(audit.Success);
    }

    // Proves app::E2.S2.A3 — revoked link refused, audited as failure.
    [Fact]
    public async Task Revoked_link_is_refused()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(status: LinkStatus.Revoked);

        var result = await h.Service.ExecuteLinkAsync(link.Token, null, null);

        Assert.Equal(LinkExecutionStatus.Revoked, result.Status);
        var audit = Assert.Single(await h.AuditsForAsync(link.Id));
        Assert.Equal("ExecutionAttempt", audit.EventType);
        Assert.False(audit.Success);
    }

    // Proves app::E2.S2.A4 and app::E1.S4.A3 — overdue link is lazily marked expired,
    // refused, and its home trigger is cleaned up (not leaked forever).
    [Fact]
    public async Task Overdue_link_is_marked_expired_and_refused()
    {
        using var h = new LinkServiceHarness();
        var now = DateTimeOffset.UtcNow;
        var link = await h.SeedLinkAsync(validFrom: now.AddHours(-3), validUntil: now.AddHours(-1));

        var result = await h.Service.ExecuteLinkAsync(link.Token, null, null);

        Assert.Equal(LinkExecutionStatus.Expired, result.Status);
        Assert.Equal(LinkStatus.Expired, link.Status);
        Assert.Contains(link.WebhookId, h.Ha.DeletedAutomations);
        var audits = await h.AuditsForAsync(link.Id);
        Assert.Single(audits, a => a.EventType == "ExecutionAttempt" && !a.Success);
        Assert.Single(audits, a => a.EventType == "WebhookDeleted");
    }

    // Proves app::E2.S2.A5 — link before its window refused as not-yet-valid, stays active.
    [Fact]
    public async Task Link_before_window_is_refused_and_stays_active()
    {
        using var h = new LinkServiceHarness();
        var now = DateTimeOffset.UtcNow;
        var link = await h.SeedLinkAsync(validFrom: now.AddHours(1), validUntil: now.AddHours(2));

        var result = await h.Service.ExecuteLinkAsync(link.Token, null, null);

        Assert.Equal(LinkExecutionStatus.NotYetValid, result.Status);
        Assert.Equal(LinkStatus.Active, link.Status);
        Assert.Equal(0, link.UsageCount);
        var audit = Assert.Single(await h.AuditsForAsync(link.Id));
        Assert.False(audit.Success);
    }

    // Proves app::E2.S2.A6 — valid use succeeds, increments count, audited as success.
    [Fact]
    public async Task Valid_use_succeeds_and_is_counted()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(maxUses: 3);

        var result = await h.Service.ExecuteLinkAsync(link.Token, "1.2.3.4", "test-agent");

        Assert.Equal(LinkExecutionStatus.Success, result.Status);
        Assert.Equal(1, link.UsageCount);
        var audits = await h.AuditsForAsync(link.Id);
        var executed = Assert.Single(audits, a => a.EventType == "Executed");
        Assert.True(executed.Success);
        Assert.Contains("1/3", executed.Description);
    }

    // Proves app::E2.S3.A1 — final use retires the link and removes the home trigger.
    [Fact]
    public async Task Final_use_retires_link_and_removes_trigger()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(maxUses: 1);

        var result = await h.Service.ExecuteLinkAsync(link.Token, null, null);

        Assert.Equal(LinkExecutionStatus.Success, result.Status);
        Assert.Equal(LinkStatus.Used, link.Status);
        Assert.Contains(link.WebhookId, h.Ha.DeletedAutomations);
        var audits = await h.AuditsForAsync(link.Id);
        Assert.Single(audits, a => a.EventType == "WebhookDeleted");
    }

    // Proves app::E2.S3.A2 — trigger-removal failure: the use still counts and is audited.
    [Fact]
    public async Task Trigger_removal_failure_does_not_undo_the_use()
    {
        using var h = new LinkServiceHarness();
        h.Ha.ThrowOnDelete = new InvalidOperationException("HA unreachable");
        var link = await h.SeedLinkAsync(maxUses: 1);

        var result = await h.Service.ExecuteLinkAsync(link.Token, null, null);

        Assert.Equal(LinkExecutionStatus.Error, result.Status);
        Assert.Equal(1, link.UsageCount);
        Assert.Equal(LinkStatus.Used, link.Status);
        var audits = await h.AuditsForAsync(link.Id);
        Assert.Single(audits, a => a.EventType == "Executed" && a.Success);
        Assert.Single(audits, a => a.EventType == "ExecutionException");
    }

    // Proves app::E2.S3.A3 — a use below the allowance keeps the link active and hosted.
    [Fact]
    public async Task Use_below_allowance_keeps_link_active()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(maxUses: 2);

        var result = await h.Service.ExecuteLinkAsync(link.Token, null, null);

        Assert.Equal(LinkExecutionStatus.Success, result.Status);
        Assert.Equal(LinkStatus.Active, link.Status);
        Assert.Empty(h.Ha.DeletedAutomations);
    }
}
