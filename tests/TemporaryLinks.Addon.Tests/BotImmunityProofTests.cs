using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TemporaryLinks.Addon.Configuration;
using TemporaryLinks.Addon.Services;
using Xunit;

namespace TemporaryLinks.Addon.Tests;

public class BotImmunityProofTests
{
    private static HomeAssistantService NewHaService(CapturingHandler handler, string? publicUrl) =>
        new(
            new HttpClient(handler),
            Options.Create(new AddonConfiguration
            {
                HaUrl = "http://ha.test:8123",
                HaToken = "test-token",
                PublicUrl = publicUrl,
            }),
            NullLogger<HomeAssistantService>.Instance);

    // Proves app::E2.S5.A1 — with the confirm page in play, the trigger only accepts
    // POST, and preview bots only ever issue GET: a prefetch can neither run the
    // actions nor consume a use.
    [Fact]
    public async Task Trigger_is_post_only_when_the_confirm_page_is_configured()
    {
        var handler = new CapturingHandler();
        var ha = NewHaService(handler, "https://example.ui.nabu.casa");
        var now = DateTimeOffset.UtcNow;

        await ha.CreateWebhookAutomationAsync("tok123", "Gate", "[]", now, now.AddHours(1));

        var post = Assert.Single(handler.Requests,
            r => r.Method == HttpMethod.Post && r.Path.Contains("config/automation/config/"));
        using var config = JsonDocument.Parse(post.Body!);
        var methods = config.RootElement.GetProperty("trigger")[0]
            .GetProperty("allowed_methods").EnumerateArray().Select(m => m.GetString()).ToList();
        Assert.Equal(["POST"], methods);
    }

    // Without a public URL the legacy direct GET link keeps working (feature is opt-in).
    [Fact]
    public async Task Trigger_stays_get_when_no_public_url_is_configured()
    {
        var handler = new CapturingHandler();
        var ha = NewHaService(handler, null);
        var now = DateTimeOffset.UtcNow;

        await ha.CreateWebhookAutomationAsync("tok123", "Gate", "[]", now, now.AddHours(1));

        using var config = JsonDocument.Parse(handler.Requests.Single().Body!);
        var methods = config.RootElement.GetProperty("trigger")[0]
            .GetProperty("allowed_methods").EnumerateArray().Select(m => m.GetString()).ToList();
        Assert.Equal(["GET"], methods);
    }

    // Proves app::E2.S5.A2 — the shared URL is the confirm page with the cloudhook only
    // in the fragment (never sent to any server), and the page fires it with one explicit
    // form POST gesture.
    [Fact]
    public async Task Share_url_is_the_confirm_page_with_hook_in_fragment()
    {
        using var h = new LinkServiceHarness(publicUrl: "https://example.ui.nabu.casa/");
        var link = await h.SeedLinkAsync();

        var url = h.Service.GetShareUrl(link);

        Assert.StartsWith("https://example.ui.nabu.casa/local/temporary_links/open.html#", url);
        Assert.Contains(Uri.EscapeDataString(link.CloudhookUrl), url);

        // The delivered SMS carries the confirm-page URL, not the raw hook.
        await h.Service.SendSmsAsync(link);
        Assert.Contains("/local/temporary_links/open.html#", h.Twilio.Sent.Single().Message);

        // The page itself: one form POST, restricted to the HA relay (no open redirect).
        Assert.Contains("method=\"post\"", SharePage.Html);
        Assert.Contains("https://hooks.nabu.casa/", SharePage.Html);
        Assert.Contains("location.hash", SharePage.Html);
    }

    // Without the feature, the share URL is the raw cloudhook (unchanged behaviour).
    [Fact]
    public async Task Share_url_is_the_raw_cloudhook_without_public_url()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync();

        Assert.Equal(link.CloudhookUrl, h.Service.GetShareUrl(link));
    }
}

public class PublicUrlDiscoveryProofTests
{
    private static AddonConfiguration NewConfig(string? publicUrl = null) => new()
    {
        HaUrl = "http://ha.test:8123",
        HaToken = "test-token",
        PublicUrl = publicUrl,
    };

    // Proves app::E2.S5.A3 — with no manual public_url, the URL is discovered from the home.
    [Fact]
    public async Task Public_url_is_discovered_from_the_home_when_not_configured()
    {
        var ha = new FakeHomeAssistantService { RemoteUiUrl = "https://abc123.ui.nabu.casa" };
        var config = NewConfig();

        var resolved = await PublicUrlResolver.ResolveAsync(
            config, ha, NullLogger.Instance);

        Assert.Equal("https://abc123.ui.nabu.casa", resolved);
        Assert.Equal("https://abc123.ui.nabu.casa", config.PublicUrl);
    }

    // Manual configuration is an override — discovery is not even consulted.
    [Fact]
    public async Task Configured_public_url_overrides_discovery()
    {
        var ha = new FakeHomeAssistantService { RemoteUiUrl = "https://abc123.ui.nabu.casa" };
        var config = NewConfig(publicUrl: "https://my.own.domain");

        var resolved = await PublicUrlResolver.ResolveAsync(
            config, ha, NullLogger.Instance);

        Assert.Equal("https://my.own.domain", resolved);
    }

    // Neither configured nor discoverable → null, and links fall back to the direct form.
    [Fact]
    public async Task Without_remote_access_links_fall_back_to_direct_form()
    {
        var ha = new FakeHomeAssistantService { RemoteUiUrl = null };
        var config = NewConfig();

        var resolved = await PublicUrlResolver.ResolveAsync(
            config, ha, NullLogger.Instance);

        Assert.Null(resolved);
        Assert.Null(config.PublicUrl);
    }
}

public class SharedPageProofTests
{
    // Proves app::E2.S5.A4 — the shared page wins over self-hosting, and the trigger URL
    // rides only in the fragment (the shared host never receives it).
    [Fact]
    public async Task Shared_page_takes_precedence_and_carries_hook_only_in_fragment()
    {
        using var h = new LinkServiceHarness(
            publicUrl: "https://example.ui.nabu.casa",
            sharePageUrl: "https://bohn.github.io/ha-temporary-links/open.html");
        var link = await h.SeedLinkAsync();

        var url = h.Service.GetShareUrl(link);

        Assert.StartsWith("https://bohn.github.io/ha-temporary-links/open.html#", url);
        var beforeFragment = url.Split('#')[0];
        Assert.DoesNotContain("hooks.nabu.casa", beforeFragment);
        Assert.DoesNotContain("?", beforeFragment);
        Assert.Contains(Uri.EscapeDataString(link.CloudhookUrl), url.Split('#')[1]);
    }

    // The shared page also makes the trigger POST-only — same bot immunity as self-hosting.
    [Fact]
    public async Task Shared_page_makes_trigger_post_only()
    {
        var handler = new CapturingHandler();
        var ha = new HomeAssistantService(
            new HttpClient(handler),
            Options.Create(new AddonConfiguration
            {
                HaUrl = "http://ha.test:8123",
                HaToken = "test-token",
                SharePageUrl = "https://bohn.github.io/ha-temporary-links/open.html",
            }),
            NullLogger<HomeAssistantService>.Instance);
        var now = DateTimeOffset.UtcNow;

        await ha.CreateWebhookAutomationAsync("tok123", "Gate", "[]", now, now.AddHours(1));

        using var config = JsonDocument.Parse(handler.Requests.Single().Body!);
        var methods = config.RootElement.GetProperty("trigger")[0]
            .GetProperty("allowed_methods").EnumerateArray().Select(m => m.GetString()).ToList();
        Assert.Equal(["POST"], methods);
    }

    // The published copy in sharepage/ must keep the embedded page's safety invariants.
    [Fact]
    public void Published_page_matches_the_embedded_page_invariants()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "ha-temporary-links.sln")))
            root = Path.GetDirectoryName(root)!;
        var published = File.ReadAllText(Path.Combine(root, "sharepage", "open.html"));

        Assert.Contains("method=\"post\"", published);
        Assert.Contains("https://hooks.nabu.casa/", published);
        Assert.Contains("location.hash", published);
        Assert.Contains("noindex", published);
    }
}
