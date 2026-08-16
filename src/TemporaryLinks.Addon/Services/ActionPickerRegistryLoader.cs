using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TemporaryLinks.Addon.Services;

/// <summary>A page model that offers the shared action picker (link creation, template
/// create/edit): the services/entities registries it renders from, and whether they loaded.</summary>
public interface IActionPickerSource
{
    bool PickerAvailable { get; set; }
    string ServicesJson { get; set; }
    string EntitiesJson { get; set; }
}

/// <summary>Loads the services/entities registries the action picker needs, identically for
/// every page that offers it. The picker is a convenience — a page using this loader must keep
/// working (raw JSON stays editable) when the home is unreachable.</summary>
public static class ActionPickerRegistryLoader
{
    public static async Task LoadAsync(
        IActionPickerSource target, IHomeAssistantService haService, ILogger logger)
    {
        try
        {
            var services = await haService.GetServicesAsync();
            var entities = await haService.GetEntitiesAsync();
            target.ServicesJson = JsonSerializer.Serialize(
                services.Select(s => new { domain = s.Domain, service = s.Service, name = s.Name }));
            target.EntitiesJson = JsonSerializer.Serialize(
                entities.Select(e => new { entityId = e.EntityId, friendlyName = e.FriendlyName }));
            target.PickerAvailable = services.Count > 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load HA registries for the action picker");
            target.PickerAvailable = false;
        }
    }
}
