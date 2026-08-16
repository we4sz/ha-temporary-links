using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;
using Xunit;

namespace TemporaryLinks.Addon.Tests;

/// <summary>
/// A press the add-on never saw still happened in the home. It is counted and recorded when the
/// add-on comes back — but the actions are NEVER run late: direct when called, or not at all.
/// </summary>
public class OfflineReconcileProofTests
{
    // Proves app::E7.S1.A3 — a trigger fired while the add-on was offline is counted and
    // audited on reconnect, and the actions are not executed late.
    [Fact]
    public async Task A_press_missed_while_offline_is_counted_and_audited_but_never_executed()
    {
        using var h = new LinkServiceHarness();
        var now = DateTimeOffset.UtcNow;
        var link = await h.SeedLinkAsync(
            maxUses: 3,
            validFrom: now.AddHours(-2),
            validUntil: now.AddHours(2),
            lastTriggerProcessedAt: now.AddMinutes(-30));
        h.Ha.LastTriggered[link.WebhookId] = now.AddMinutes(-10);

        var reconciled = await h.Service.ReconcileOfflineTriggersAsync();

        Assert.Equal(1, reconciled);
        Assert.Equal(1, link.UsageCount);
        Assert.Equal(LinkStatus.Active, link.Status);
        Assert.Equal(0, h.Ha.ExecutedActionsCount); // never run late
        var audit = Assert.Single(await h.AuditsForAsync(link.Id), a => a.EventType == "OfflineUse");
        Assert.Contains("were not executed", audit.Description);
        Assert.Contains("1/3", audit.Description);
        Assert.False(audit.Success);
    }

    // Proves app::E7.S1.A3 — a missed press that spends the allowance retires the link and
    // takes its trigger out of the home.
    [Fact]
    public async Task A_missed_press_that_spends_the_allowance_retires_the_link()
    {
        using var h = new LinkServiceHarness();
        var now = DateTimeOffset.UtcNow;
        var link = await h.SeedLinkAsync(maxUses: 1, lastTriggerProcessedAt: now.AddMinutes(-30));
        var webhookId = link.WebhookId;
        h.Ha.LastTriggered[link.WebhookId] = now.AddMinutes(-5);

        await h.Service.ReconcileOfflineTriggersAsync();

        Assert.Equal(1, link.UsageCount);
        Assert.Equal(LinkStatus.Used, link.Status);
        Assert.Contains(webhookId, h.Ha.DeletedAutomations);
        Assert.Equal(0, h.Ha.ExecutedActionsCount);
    }

    // Proves app::E7.S1.A3 — reconciliation is not repeatable: the same reported press is
    // counted once, however many times the add-on reconnects.
    [Fact]
    public async Task The_same_missed_press_is_never_counted_twice()
    {
        using var h = new LinkServiceHarness();
        var now = DateTimeOffset.UtcNow;
        var link = await h.SeedLinkAsync(maxUses: 5, lastTriggerProcessedAt: now.AddMinutes(-30));
        h.Ha.LastTriggered[link.WebhookId] = now.AddMinutes(-10);

        await h.Service.ReconcileOfflineTriggersAsync();
        await h.Service.ReconcileOfflineTriggersAsync();
        await h.Service.ReconcileOfflineTriggersAsync();

        Assert.Equal(1, link.UsageCount);
        Assert.Single(await h.AuditsForAsync(link.Id), a => a.EventType == "OfflineUse");
    }

    // Proves app::E7.S1.A3 — a press the add-on DID process is not counted again: handling an
    // event moves the watermark past the home's record of that run.
    [Fact]
    public async Task A_press_the_addon_processed_is_not_reconciled_again()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(maxUses: 3);
        h.Ha.LastTriggered[link.WebhookId] = DateTimeOffset.UtcNow;

        await h.Service.ExecuteLinkAsync(link.Token, "webhook", "test");
        var reconciled = await h.Service.ReconcileOfflineTriggersAsync();

        Assert.Equal(0, reconciled);
        Assert.Equal(1, link.UsageCount);
        Assert.Equal(1, h.Ha.ExecutedActionsCount);
    }

    // A link the add-on has no watermark for (issued before the add-on kept one) adopts what
    // the home reports rather than counting a press that may long since be accounted for.
    [Fact]
    public async Task A_link_with_no_watermark_adopts_the_homes_record_without_counting()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(maxUses: 3, lastTriggerProcessedAt: null);
        var fired = DateTimeOffset.UtcNow.AddMinutes(-10);
        h.Ha.LastTriggered[link.WebhookId] = fired;

        var reconciled = await h.Service.ReconcileOfflineTriggersAsync();

        Assert.Equal(0, reconciled);
        Assert.Equal(0, link.UsageCount);
        Assert.Empty(await h.AuditsForAsync(link.Id));
        Assert.Equal(fired, link.LastTriggerProcessedAt);
    }

    // A press the home itself refused while the add-on was offline (outside the window) is
    // recorded as the refusal it was — no use is taken for something that never ran.
    [Fact]
    public async Task A_missed_press_outside_the_window_is_audited_but_costs_no_use()
    {
        using var h = new LinkServiceHarness();
        var now = DateTimeOffset.UtcNow;
        var link = await h.SeedLinkAsync(
            maxUses: 2,
            validFrom: now.AddMinutes(-20),
            validUntil: now.AddHours(2),
            lastTriggerProcessedAt: now.AddHours(-1));
        h.Ha.LastTriggered[link.WebhookId] = now.AddMinutes(-40); // before the window opened

        var reconciled = await h.Service.ReconcileOfflineTriggersAsync();

        Assert.Equal(0, reconciled);
        Assert.Equal(0, link.UsageCount);
        Assert.Equal(LinkStatus.Active, link.Status);
        var audit = Assert.Single(await h.AuditsForAsync(link.Id));
        Assert.Equal("ExecutionAttempt", audit.EventType);
        Assert.False(audit.Success);
        Assert.Contains("outside the validity window", audit.Description);
    }

    // A missed press on a link whose allowance is already gone is audited and retires the link
    // — it never pushes the count past the allowance.
    [Fact]
    public async Task A_missed_press_with_no_allowance_left_is_audited_and_retires_the_link()
    {
        using var h = new LinkServiceHarness();
        var now = DateTimeOffset.UtcNow;
        var link = await h.SeedLinkAsync(
            maxUses: 1, usageCount: 1, status: LinkStatus.Active,
            lastTriggerProcessedAt: now.AddMinutes(-30));
        var webhookId = link.WebhookId;
        h.Ha.LastTriggered[link.WebhookId] = now.AddMinutes(-5);

        await h.Service.ReconcileOfflineTriggersAsync();

        Assert.Equal(1, link.UsageCount);
        Assert.Equal(LinkStatus.Used, link.Status);
        Assert.Contains(webhookId, h.Ha.DeletedAutomations);
        Assert.Single(await h.AuditsForAsync(link.Id),
            a => a.EventType == "ExecutionAttempt" && !a.Success);
        Assert.Equal(0, h.Ha.ExecutedActionsCount);
    }
}
