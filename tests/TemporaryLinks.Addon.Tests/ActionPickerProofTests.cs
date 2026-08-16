using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TemporaryLinks.Addon.Configuration;
using TemporaryLinks.Addon.Services;
using Xunit;

namespace TemporaryLinks.Addon.Tests;

/// <summary>Serves canned JSON bodies per path suffix.</summary>
public sealed class CannedHandler : HttpMessageHandler
{
    public Dictionary<string, string> Responses { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = Responses.FirstOrDefault(kv => path.EndsWith(kv.Key)).Value ?? "[]";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        });
    }
}

public class ActionPickerProofTests
{
    private static HomeAssistantService NewHaService(CannedHandler handler) =>
        new(
            new HttpClient(handler),
            Options.Create(new AddonConfiguration
            {
                HaUrl = "http://ha.test:8123",
                HaToken = "test-token",
                DefaultMessageTemplate = "{link}",
            }),
            NullLogger<HomeAssistantService>.Instance);

    // Proves app::E1.S7.A1 — the pickable actions and entities come from the home's
    // live registries, fetched from its API.
    [Fact]
    public async Task Registries_are_fetched_live_from_the_home()
    {
        var handler = new CannedHandler();
        handler.Responses["/api/services"] = """
            [
              {"domain": "light", "services": {"turn_on": {"name": "Turn on"}, "turn_off": {"name": "Turn off"}}},
              {"domain": "lock", "services": {"unlock": {"name": "Unlock"}}}
            ]
            """;
        handler.Responses["/api/states"] = """
            [
              {"entity_id": "light.hall", "state": "off", "attributes": {"friendly_name": "Hall light"}},
              {"entity_id": "lock.front_door", "state": "locked", "attributes": {"friendly_name": "Front door"}}
            ]
            """;
        var ha = NewHaService(handler);

        var services = await ha.GetServicesAsync();
        var entities = await ha.GetEntitiesAsync();

        Assert.Equal(3, services.Count);
        Assert.Contains(services, s => s.Domain == "lock" && s.Service == "unlock" && s.Name == "Unlock");
        Assert.Equal(2, entities.Count);
        Assert.Contains(entities, e => e.EntityId == "light.hall" && e.FriendlyName == "Hall light");
    }
}
