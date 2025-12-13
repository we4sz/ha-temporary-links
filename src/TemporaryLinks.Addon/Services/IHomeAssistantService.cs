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
}

public record CloudhookResult(string WebhookId, string CloudhookId, string CloudhookUrl);
