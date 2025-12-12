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
}

public record EntityInfo(string EntityId, string? FriendlyName);
