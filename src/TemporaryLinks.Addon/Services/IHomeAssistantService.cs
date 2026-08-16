using System.Text.Json;

namespace TemporaryLinks.Addon.Services;

public interface IHomeAssistantService
{
    /// <summary>Arms (or re-arms, same id) a link's home-side trigger to the current
    /// enforcement model and sharing mode. Idempotent.</summary>
    Task<string> CreateWebhookAutomationAsync(
        string token,
        string linkName,
        string actionsJson,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a link's trigger from the home. Returns true when this call removed
    /// it, false when the home reports it was already gone; throws when the home refuses,
    /// which means the trigger may still be standing.</summary>
    Task<bool> DeleteWebhookAutomationAsync(
        string automationId,
        CancellationToken cancellationToken = default);

    /// <summary>The automation config the home currently stores for an id, or null when the
    /// home has none. Used to tell a trigger armed to the current model from an older one.</summary>
    Task<JsonElement?> TryGetAutomationConfigAsync(
        string automationId,
        CancellationToken cancellationToken = default);

    /// <summary>When each automation last ran, keyed by automation id — the home's own record
    /// of trigger fires, which the add-on reconciles against what it processed.</summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> GetAutomationLastTriggeredAsync(
        CancellationToken cancellationToken = default);

    Task<CloudhookResult> CreateCloudhookAsync(
        string webhookId,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the public relay registration for a webhook (compensation when a
    /// creation fails after the cloudhook already exists).</summary>
    Task DeleteCloudhookAsync(
        string webhookId,
        CancellationToken cancellationToken = default);

    /// <summary>Runs a link's actions (a JSON array of service calls) against the home.
    /// Called by the add-on only after a use has been atomically claimed, so the allowance
    /// binds the actions rather than being reconciled after the fact.</summary>
    Task ExecuteActionsAsync(
        string actionsJson,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HaServiceInfo>> GetServicesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HaEntityInfo>> GetEntitiesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>The home's public remote-UI base URL (e.g. https://xxx.ui.nabu.casa)
    /// discovered via HA Cloud, or null when remote access is unavailable.</summary>
    Task<string?> GetRemoteUiUrlAsync(CancellationToken cancellationToken = default);
}

public record CloudhookResult(string WebhookId, string CloudhookId, string CloudhookUrl);

public record HaServiceInfo(string Domain, string Service, string? Name);

public record HaEntityInfo(string EntityId, string? FriendlyName);
