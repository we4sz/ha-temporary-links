namespace TemporaryLinks.Addon.Services;

public interface IHomeAssistantService
{
    Task<bool> CallScriptAsync(
        string scriptEntityId,
        string? dataJson = null,
        CancellationToken cancellationToken = default);

    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EntityInfo>> GetEntitiesAsync(
        string? domainFilter = null,
        CancellationToken cancellationToken = default);

    Task<HaConfig?> GetConfigAsync(CancellationToken cancellationToken = default);

    Task<string?> CreateWebhookAutomationAsync(
        string token,
        string linkName,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteWebhookAutomationAsync(
        string automationId,
        CancellationToken cancellationToken = default);

    Task<CloudhookResult?> CreateCloudhookAsync(
        string webhookId,
        CancellationToken cancellationToken = default);
}

public record EntityInfo(string EntityId, string? FriendlyName);

public record HaConfig(string? ExternalUrl, string? InternalUrl);

public record CloudhookResult(string WebhookId, string CloudhookId, string CloudhookUrl);
