using Microsoft.EntityFrameworkCore;
using TemporaryLinks.Addon.Models;
using Xunit;

namespace TemporaryLinks.Addon.Tests;

public class RecipientOptionalProofTests
{
    // Proves app::E1.S6.A1 (and app::E1.S1.A1) — a link can be created with no recipient.
    [Fact]
    public async Task Link_can_be_created_without_a_recipient()
    {
        using var h = new LinkServiceHarness();
        var now = DateTimeOffset.UtcNow;

        var link = await h.Service.CreateLinkAsync(
            "Gate", now, now.AddHours(1), recipientPhoneNumber: null,
            customMessage: null, createdBy: "test", actions: "[]");

        Assert.Equal(LinkStatus.Active, link.Status);
        Assert.Null(link.RecipientPhoneNumber);
        Assert.NotNull(await h.Db.TemporaryLinks.SingleAsync(l => l.Id == link.Id));
        Assert.False(string.IsNullOrEmpty(link.CloudhookUrl));
    }

    // Proves app::E1.S6.A2 — SMS is refused with an explanation when there is no recipient.
    [Fact]
    public async Task Sms_is_refused_without_a_recipient()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(recipientPhone: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Service.SendSmsAsync(link));

        Assert.Contains("no recipient", ex.Message);
        Assert.Empty(h.Twilio.Sent);
    }

    // Proves app::E1.S6.A3 — amending a recipient onto the link makes SMS available.
    [Fact]
    public async Task Adding_a_recipient_enables_sms()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(recipientPhone: null);

        await h.Service.UpdateLinkAsync(
            link.Id, link.ValidFrom, link.ValidUntil, "+15559876543", null, link.MaxUses);
        await h.Service.SendSmsAsync(link);

        var sent = Assert.Single(h.Twilio.Sent);
        Assert.Equal("+15559876543", sent.To);
    }

    // Proves app::E3.S2.A3 — the delivery record reflects provider acceptance.
    [Fact]
    public async Task Delivery_record_reflects_provider_acceptance()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync();

        await h.Service.SendSmsAsync(link);

        var record = Assert.Single(h.Db.LinkSmsAudits);
        Assert.True(record.SmsSent);
        Assert.False(string.IsNullOrEmpty(record.TwilioMessageSid));
    }

    // Proves app::E5.S2.A1 — filtering by status returns only links of that status.
    [Fact]
    public async Task Status_filter_returns_only_matching_links()
    {
        using var h = new LinkServiceHarness();
        await h.SeedLinkAsync(status: LinkStatus.Active);
        await h.SeedLinkAsync(status: LinkStatus.Used);
        await h.SeedLinkAsync(status: LinkStatus.Revoked);

        var used = await h.Service.GetLinksAsync("Used");
        var all = await h.Service.GetLinksAsync(null);

        Assert.Single(used);
        Assert.All(used, l => Assert.Equal(LinkStatus.Used, l.Status));
        Assert.Equal(3, all.Count);
    }
}
