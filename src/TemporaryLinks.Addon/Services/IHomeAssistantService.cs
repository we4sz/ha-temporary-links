namespace TemporaryLinks.Addon.Services;

public interface IHomeAssistantService
{
    Task<string> CreateWebhookAutomationAsync(
        string token,
        string linkName,
        string actionsJson,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        CancellationToken cancellationToken = default);

    Task DeleteWebhookAutomationAsync(
        string automationId,
        CancellationToken cancellationToken = default);

    Task<CloudhookResult> CreateCloudhookAsync(
        string webhookId,
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
