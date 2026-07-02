using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TemporaryLinks.Addon.Configuration;
using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;
using Xunit;

namespace TemporaryLinks.Addon.Tests;

/// <summary>Captures every request and answers 200 OK with an empty JSON body.</summary>
public sealed class CapturingHandler : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}"),
        };
    }
}

public class GrantEnforcementProofTests
{
    private static HomeAssistantService NewHaService(CapturingHandler handler) =>
        new(
            new HttpClient(handler),
            Options.Create(new AddonConfiguration
            {
                HaUrl = "http://ha.test:8123",
                HaToken = "test-token",
                DefaultMessageTemplate = "{link}",
            }),
            NullLogger<HomeAssistantService>.Instance);

    // Proves app::E2.S1.A3 — the trigger automation carries a guard condition on the
    // validity window, so the home itself refuses to run the actions outside the grant.
    [Fact]
    public async Task Automation_config_guards_the_validity_window()
    {
        var handler = new CapturingHandler();
        var ha = NewHaService(handler);
        var from = new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero);
        var until = new DateTimeOffset(2026, 7, 2, 18, 0, 0, TimeSpan.Zero);

        await ha.CreateWebhookAutomationAsync("tok123", "Gate", "[]", from, until);

        var post = Assert.Single(handler.Requests,
            r => r.Method == HttpMethod.Post && r.Path.Contains("config/automation/config/"));
        using var config = JsonDocument.Parse(post.Body!);
        var condition = config.RootElement.GetProperty("condition");
        Assert.Equal(JsonValueKind.Array, condition.ValueKind);
        var template = condition[0].GetProperty("value_template").GetString()!;
        Assert.Contains(from.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss"), template);
        Assert.Contains(until.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss"), template);
        Assert.Contains("now()", template);
    }

    // Proves app::E1.S2.A5 (and re-proves app::E1.S2.A1) — amending the window re-arms
    // the home-side guard before the amendment is saved, and audits the amendment.
    [Fact]
    public async Task Amending_the_window_rearms_the_home_side_guard()
    {
        using var h = new LinkServiceHarness();
        var link = await h.SeedLinkAsync(maxUses: 2);
        var newFrom = DateTimeOffset.UtcNow.AddHours(2);
        var newUntil = DateTimeOffset.UtcNow.AddHours(4);

        await h.Service.UpdateLinkAsync(
            link.Id, newFrom, newUntil, "+15550000000", "hello", 2);

        var armed = Assert.Single(h.Ha.ArmedWindows);
        Assert.Equal(newFrom, armed.ValidFrom);
        Assert.Equal(newUntil, armed.ValidUntil);
        Assert.Equal(newFrom, link.ValidFrom);
        Assert.Equal("+15550000000", link.RecipientPhoneNumber);
        var audits = await h.AuditsForAsync(link.Id);
        Assert.Single(audits, a => a.EventType == "Updated");
    }

    // Proves app::E1.S1.A4 — if the cloudhook cannot be created, the just-created
    // automation is compensated away and no link is persisted.
    [Fact]
    public async Task Failed_creation_leaves_no_orphaned_trigger_and_no_link()
    {
        using var h = new LinkServiceHarness();
        h.Ha.ThrowOnCloudhook = new InvalidOperationException("HA Cloud unavailable");
        var now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.CreateLinkAsync(
                "Gate", now, now.AddHours(1), "+15551234567", null, "test", "[]"));

        var created = Assert.Single(h.Ha.CreatedAutomations);
        Assert.Contains(created, h.Ha.DeletedAutomations);
        Assert.Empty(h.Db.TemporaryLinks);
    }

    // Proves app::E1.S3.A1 failure path — a home-side failure during revoke does not
    // undo the revocation, and the failure is audited.
    [Fact]
    public async Task Revoke_survives_home_side_cleanup_failure()
    {
        using var h = new LinkServiceHarness();
        h.Ha.ThrowOnDelete = new InvalidOperationException("HA unreachable");
        var link = await h.SeedLinkAsync();

        var revoked = await h.Service.RevokeLinkAsync(link.Token);

        Assert.True(revoked);
        Assert.Equal(LinkStatus.Revoked, link.Status);
        var audits = await h.AuditsForAsync(link.Id);
        Assert.Single(audits, a => a.EventType == "Revoked");
        Assert.Single(audits, a => a.EventType == "ExecutionException");
    }
}
