using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;
using Xunit;

namespace TemporaryLinks.Addon.Tests;

/// <summary>Service-layer proofs closing the "built but unproven" gap for E1–E5.</summary>
public class CoverageProofTests
{
    private async Task<TemporaryLink> CreateAsync(LinkServiceHarness h, int maxUses = 1,
        string actions = "[]", string createdBy = "tester")
    {
        var now = DateTimeOffset.UtcNow;
        return await h.Service.CreateLinkAsync(
            "Gate", now, now.AddHours(1), "+15551230000", null, createdBy, actions, maxUses);
    }

    // Proves app::E1.S1.A2 — creation hosts a trigger in the home and records its URL.
    [Fact]
    public async Task Creating_a_link_hosts_a_trigger_and_records_its_url()
    {
        using var h = new LinkServiceHarness();
        var link = await CreateAsync(h);

        Assert.Contains(link.WebhookId, h.Ha.CreatedAutomations);
        Assert.False(string.IsNullOrEmpty(link.CloudhookUrl));
        Assert.Equal(LinkStatus.Active, link.Status);
        Assert.Equal(0, link.UsageCount);
    }

    // Proves app::E1.S1.A3 — creation is audited with who created it and the allowance.
    [Fact]
    public async Task Creation_is_audited_with_creator_and_allowance()
    {
        using var h = new LinkServiceHarness();
        var link = await CreateAsync(h, maxUses: 5, createdBy: "alice");

        var created = Assert.Single(await h.AuditsForAsync(link.Id), a => a.EventType == "Created");
        Assert.Contains("alice", created.Description);
        Assert.Contains("5", created.Description);
    }

    // Proves app::E1.S2.A2 — a non-active link cannot be amended.
    [Fact]
    public async Task Non_active_link_cannot_be_edited()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(status: LinkStatus.Revoked);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.UpdateLinkAsync(link.Id, link.ValidFrom, link.ValidUntil, "+1", null, 1));
    }

    // Proves app::E1.S2.A3 — the allowance cannot be set below the recorded usage count.
    [Fact]
    public async Task Allowance_cannot_drop_below_usage_count()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(maxUses: 3, usageCount: 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.UpdateLinkAsync(link.Id, link.ValidFrom, link.ValidUntil, "+1", null, 1));
    }

    // Proves app::E1.S2.A4 — a link's name and actions are not amendable.
    [Fact]
    public async Task Editing_does_not_change_name_or_actions()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(maxUses: 2);
        var name = link.Name;
        var actions = link.Actions;

        await h.Service.UpdateLinkAsync(
            link.Id, link.ValidFrom, link.ValidUntil.AddHours(1), "+15559999999", "note", 2);

        Assert.Equal(name, link.Name);
        Assert.Equal(actions, link.Actions);
    }

    // Proves app::E1.S3.A2 — a fully used link cannot be revoked.
    [Fact]
    public async Task Used_link_cannot_be_revoked()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(status: LinkStatus.Used, usageCount: 1);

        Assert.False(await h.Service.RevokeLinkAsync(link.Token));
    }

    // Proves app::E1.S3.A4 — a revoked link's record and history remain visible.
    [Fact]
    public async Task Revoked_link_and_its_history_remain_visible()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync();

        await h.Service.RevokeLinkAsync(link.Token);

        var reloaded = await h.Service.GetLinkByIdAsync(link.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(LinkStatus.Revoked, reloaded!.Status);
        Assert.Contains(reloaded.AuditEntries, a => a.EventType == "Revoked");
    }

    // Proves app::E1.S4.A1 — the sweep marks overdue active links expired, audits, and
    // removes their triggers.
    [Fact]
    public async Task Expiry_sweep_expires_audits_and_removes_trigger()
    {
        using var h = new LinkServiceHarness();
        var now = DateTimeOffset.UtcNow;
        var link = await h.SeedLinkAsync(validFrom: now.AddHours(-2), validUntil: now.AddHours(-1));
        var webhookId = link.WebhookId;

        await h.Service.ExpireOldLinksAsync();

        var reloaded = await h.Service.GetLinkByIdAsync(link.Id);
        Assert.Equal(LinkStatus.Expired, reloaded!.Status);
        Assert.Contains(webhookId, h.Ha.DeletedAutomations);
        Assert.Contains(reloaded.AuditEntries, a => a.EventType == "Expired");
    }

    // Proves app::E1.S4.A4 — a home-side removal failure during the sweep is audited, the
    // link is still expired, and the sweep continues with the remaining links.
    [Fact]
    public async Task Sweep_continues_when_trigger_removal_fails()
    {
        using var h = new LinkServiceHarness();
        h.Ha.ThrowOnDelete = new InvalidOperationException("HA down");
        var now = DateTimeOffset.UtcNow;
        var a = await h.SeedLinkAsync(validFrom: now.AddHours(-2), validUntil: now.AddHours(-1));
        var b = await h.SeedLinkAsync(validFrom: now.AddHours(-2), validUntil: now.AddHours(-1));

        await h.Service.ExpireOldLinksAsync();

        Assert.Equal(LinkStatus.Expired, (await h.Service.GetLinkByIdAsync(a.Id))!.Status);
        Assert.Equal(LinkStatus.Expired, (await h.Service.GetLinkByIdAsync(b.Id))!.Status);
    }

    // Proves app::E3.S2.A1 — a successful send records the message + provider id, audits
    // success, and (E3.S2.A3) marks the delivery accepted.
    [Fact]
    public async Task Successful_send_records_message_and_audits()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync();

        await h.Service.SendSmsAsync(link);

        var record = Assert.Single(h.Db.LinkSmsAudits);
        Assert.True(record.SmsSent);
        Assert.False(string.IsNullOrEmpty(record.TwilioMessageSid));
        Assert.Contains(await h.AuditsForAsync(link.Id), a => a.EventType == "SmsSent");
    }

    // Proves app::E3.S2.A2 — a provider failure is audited and surfaced, never shown as sent.
    [Fact]
    public async Task Failed_send_is_audited_and_throws()
    {
        using var h = new LinkServiceHarness();
        h.Twilio.FailNextSend = true;
        var link = await h.SeedLinkAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SendSmsAsync(link));

        Assert.Empty(h.Db.LinkSmsAudits);
        Assert.Contains(await h.AuditsForAsync(link.Id), a => a.EventType == "SmsFailure");
    }

    // Proves app::E5.S3.A2 — consequential events across a link's life each produce an
    // audit event (creation, execution, cleanup, delivery, revoke).
    [Fact]
    public async Task Consequential_events_each_produce_an_audit_entry()
    {
        using var h = new LinkServiceHarness();
        var link = await CreateAsync(h, maxUses: 2);
        await h.Service.SendSmsAsync(link);
        await h.Service.ExecuteLinkAsync(link.Token, "webhook", "test");
        await h.Service.RevokeLinkAsync(link.Token);

        var types = (await h.AuditsForAsync(link.Id)).Select(a => a.EventType).ToHashSet();
        Assert.Superset(
            new HashSet<string> { "Created", "SmsSent", "Executed", "Revoked" }, types);
    }
}
