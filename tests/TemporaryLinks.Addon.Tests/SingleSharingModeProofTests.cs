using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TemporaryLinks.Addon.Configuration;
using TemporaryLinks.Addon.Services;
using Xunit;

namespace TemporaryLinks.Addon.Tests;

/// <summary>
/// There is one sharing mode: the confirm page's button press. The trigger accepts that gesture
/// and nothing else, whatever the configuration says, and a link that could only be shared some
/// other way is never created at all.
/// </summary>
public class SingleSharingModeProofTests
{
    // Proves app::E2.S6.A1 — what the add-on arms no longer depends on configuration: the
    // trigger accepts the confirm page's POST and nothing else, so no direct one-tap form of
    // a link can exist even on an installation with no page configured at all.
    [Fact]
    public async Task Arming_is_post_only_even_with_no_confirm_page_configured()
    {
        var handler = new CapturingHandler();
        var ha = new HomeAssistantService(
            new HttpClient(handler),
            Options.Create(new AddonConfiguration
            {
                HaUrl = "http://ha.test:8123",
                HaToken = "test-token",
            }),
            NullLogger<HomeAssistantService>.Instance);
        var now = DateTimeOffset.UtcNow;

        await ha.CreateWebhookAutomationAsync("tok123", "Gate", "[]", now, now.AddHours(1));

        using var config = JsonDocument.Parse(handler.Requests.Single().Body!);
        var methods = config.RootElement.GetProperty("trigger")[0]
            .GetProperty("allowed_methods").EnumerateArray().Select(m => m.GetString()).ToList();
        Assert.Equal(["POST"], methods);
    }

    // Proves app::E2.S6.A1 — a link created on a cloud-connected home is shared through the
    // confirm page the add-on serves itself, with the trigger URL only in the fragment.
    [Fact]
    public async Task A_new_link_is_shared_through_the_self_hosted_confirm_page()
    {
        using var h = new LinkServiceHarness(publicUrl: "https://example.ui.nabu.casa");
        var now = DateTimeOffset.UtcNow;

        var link = await h.Service.CreateLinkAsync(
            "Gate", now, now.AddHours(1), null, null, "test", "[]");

        Assert.True(link.TriggerAcceptsPost);
        var url = h.Service.GetShareUrl(link);
        Assert.StartsWith(
            "https://example.ui.nabu.casa/local/temporary_links/open.html#", url);
        Assert.Contains(Uri.EscapeDataString(link.CloudhookUrl), url);
    }

    // Proves app::E2.S6.A1 — with a shared page configured it wins, and the shared URL is
    // still the page (never the raw trigger).
    [Fact]
    public async Task A_shared_confirm_page_wins_over_self_hosting()
    {
        using var h = new LinkServiceHarness(
            publicUrl: "https://example.ui.nabu.casa",
            sharePageUrl: "https://we4sz.github.io/ha-temporary-links/open.html");
        var now = DateTimeOffset.UtcNow;

        var link = await h.Service.CreateLinkAsync(
            "Gate", now, now.AddHours(1), null, null, "test", "[]");

        var url = h.Service.GetShareUrl(link);
        Assert.StartsWith("https://we4sz.github.io/ha-temporary-links/open.html#", url);
        Assert.Contains(Uri.EscapeDataString(link.CloudhookUrl), url.Split('#')[1]);
    }

    // Proves app::E2.S6.A2 and app::E7.S2.A1 — with no page to share (no shared page, no
    // cloud remote access) creation is refused with an explanation of what to enable, and
    // nothing is put in the home: no preview-consumable link is ever issued instead.
    [Fact]
    public async Task Creation_without_confirm_page_hosting_is_refused_and_creates_nothing()
    {
        using var h = new LinkServiceHarness();
        var now = DateTimeOffset.UtcNow;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.CreateLinkAsync("Gate", now, now.AddHours(1), null, null, "test", "[]"));

        // The explanation names both ways out.
        Assert.Contains("share_page_url", ex.Message);
        Assert.Contains("public_url", ex.Message);
        Assert.Contains("Cloud", ex.Message);

        // Nothing reached the home, and no link exists to share.
        Assert.Empty(h.Ha.CreatedAutomations);
        Assert.Empty(h.Ha.DeletedAutomations);
        Assert.Empty(h.Ha.DeletedCloudhooks);
        Assert.Empty(h.Db.TemporaryLinks);
    }

    // Proves app::E2.S6.A1 — the one-tap form does not survive an upgrade either: the boot
    // pass re-arms a link armed for the old gesture, and from then on that link is shared
    // through the confirm page like every other.
    [Fact]
    public async Task A_get_armed_link_is_rearmed_to_post_and_then_shares_through_the_page()
    {
        using var h = new LinkServiceHarness(publicUrl: "https://example.ui.nabu.casa");
        var link = await h.SeedLinkAsync(triggerAcceptsPost: false);
        h.ArmLegacyGetGesture(link);

        // Until it is re-armed it keeps handing out the URL its trigger accepts (E7.S7.A2).
        Assert.Equal(link.CloudhookUrl, h.Service.GetShareUrl(link));

        var result = await h.Service.RearmTriggersAsync();

        Assert.Equal(1, result.Rearmed);
        Assert.True(link.TriggerAcceptsPost);
        using var stored = JsonDocument.Parse(h.Ha.StoredAutomations[link.WebhookId]);
        Assert.Equal("POST", stored.RootElement.GetProperty("trigger")[0]
            .GetProperty("allowed_methods")[0].GetString());
        Assert.StartsWith(
            "https://example.ui.nabu.casa/local/temporary_links/open.html#",
            h.Service.GetShareUrl(link));
    }
}
