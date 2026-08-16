using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TemporaryLinks.Addon.Configuration;
using TemporaryLinks.Addon.Services;
using Xunit;

namespace TemporaryLinks.Addon.Tests;

/// <summary>
/// Creation and execution must agree on what an action is. Anything creation accepts must be
/// executable — otherwise a link is born healthy and then burns a use failing on its own form.
///
/// Every harness here has a public URL: creation requires a confirm page to share (E2.S6.A2),
/// so a refusal in these tests is always about the ACTIONS, never about the installation.
/// </summary>
public class ActionContractProofTests
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

    private static async Task<string> CreatedActionsAsync(LinkServiceHarness h, string actions)
    {
        var now = DateTimeOffset.UtcNow;
        var link = await h.Service.CreateLinkAsync(
            "Gate", now, now.AddHours(1), null, null, "test", actions);
        return link.Actions;
    }

    // Proves app::E1.S1.A6 — the home's own automation syntax ("service") is accepted at
    // creation and normalized to the contract execution enforces.
    [Fact]
    public async Task Service_key_is_normalized_to_the_execution_contract()
    {
        using var h = new LinkServiceHarness(publicUrl: "https://example.ui.nabu.casa");

        var stored = await CreatedActionsAsync(h,
            """[{"service":"lock.unlock","target":{"entity_id":"lock.front_door"}}]""");

        using var doc = JsonDocument.Parse(stored);
        var action = doc.RootElement[0];
        Assert.Equal("lock.unlock", action.GetProperty("action").GetString());
        Assert.False(action.TryGetProperty("service", out _));
        Assert.Equal("lock.front_door",
            action.GetProperty("target").GetProperty("entity_id").GetString());
    }

    // Proves app::E1.S1.A6 — a top-level entity_id (again the home's syntax) becomes a target,
    // so execution finds it where it looks.
    [Fact]
    public async Task Top_level_entity_id_becomes_a_target()
    {
        using var h = new LinkServiceHarness(publicUrl: "https://example.ui.nabu.casa");

        var stored = await CreatedActionsAsync(h,
            """[{"service":"light.turn_on","entity_id":"light.hall","data":{"brightness":200}}]""");

        using var doc = JsonDocument.Parse(stored);
        var action = doc.RootElement[0];
        Assert.Equal("light.turn_on", action.GetProperty("action").GetString());
        Assert.Equal("light.hall",
            action.GetProperty("target").GetProperty("entity_id").GetString());
        Assert.Equal(200, action.GetProperty("data").GetProperty("brightness").GetInt32());
        Assert.False(action.TryGetProperty("entity_id", out _));
    }

    // Proves app::E1.S1.A6 + app::E1.S1.A5 — a form execution would refuse is refused at
    // creation, with an explanation, and nothing is created in the home.
    [Theory]
    [InlineData("""[{"do":"something"}]""")]
    [InlineData("""[{"action":"notaservice"}]""")]
    [InlineData("""[{"action":""}]""")]
    [InlineData("""["lock.unlock"]""")]
    [InlineData("""{"action":"lock.unlock"}""")]
    [InlineData("not json at all")]
    public async Task A_form_execution_would_refuse_is_refused_at_creation(string actions)
    {
        using var h = new LinkServiceHarness(publicUrl: "https://example.ui.nabu.casa");
        var now = DateTimeOffset.UtcNow;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.CreateLinkAsync("Gate", now, now.AddHours(1), null, null, "test", actions));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Empty(h.Ha.CreatedAutomations);
        Assert.Empty(h.Db.TemporaryLinks);
    }

    // Proves app::E1.S1.A6 end to end — what creation stored is exactly what execution will
    // accept: the normalized actions really do run as service calls against the home.
    [Fact]
    public async Task What_creation_accepted_is_what_execution_runs()
    {
        using var h = new LinkServiceHarness(publicUrl: "https://example.ui.nabu.casa");
        var stored = await CreatedActionsAsync(h,
            """[{"service":"light.turn_on","entity_id":"light.hall"}]""");

        var handler = new CapturingHandler();
        await NewHaService(handler).ExecuteActionsAsync(stored);

        var call = Assert.Single(handler.Requests,
            r => r.Method == HttpMethod.Post && r.Path.EndsWith("/api/services/light/turn_on"));
        Assert.Contains("light.hall", call.Body);
    }

    // The canonical form the picker and the UI already speak stays untouched.
    [Fact]
    public async Task The_canonical_form_passes_through_unchanged()
    {
        using var h = new LinkServiceHarness(publicUrl: "https://example.ui.nabu.casa");

        var stored = await CreatedActionsAsync(h,
            """[{"action":"lock.unlock","target":{"entity_id":"lock.front_door"}}]""");

        using var doc = JsonDocument.Parse(stored);
        var action = doc.RootElement[0];
        Assert.Equal("lock.unlock", action.GetProperty("action").GetString());
        Assert.Equal("lock.front_door",
            action.GetProperty("target").GetProperty("entity_id").GetString());
        // ...and the UI renders it the same way it renders what the picker produces.
        var summary = Assert.Single(ActionFormatter.Summarize(stored));
        Assert.Equal("lock.unlock", summary.Service);
        Assert.Equal("lock.front_door", summary.Target);
    }
}
