using TemporaryLinks.Addon.Models;

namespace TemporaryLinks.Addon.Services;

public interface ILinkService
{
    Task<TemporaryLink> CreateLinkAsync(
        string name,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        string? recipientPhoneNumber,
        string? customMessage,
        string createdBy,
        string actions,
        int maxUses = 1);

    Task<TemporaryLink> UpdateLinkAsync(
        Guid id,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        string? recipientPhoneNumber,
        string? customMessage,
        int maxUses);

    Task SendSmsAsync(TemporaryLink link);
    Task<TemporaryLink?> GetLinkByIdAsync(Guid id);
    Task<IList<TemporaryLink>> GetLinksAsync(string? statusFilter = null);

    /// <summary>Judges a trigger the home announced as fired inside the validity window:
    /// on success it claims a use and runs the link's actions.</summary>
    Task<LinkExecutionResult> ExecuteLinkAsync(string token, string? ipAddress, string? userAgent);

    /// <summary>Records a trigger the home itself refused (outside the validity window).
    /// Audit only: never claims a use, never runs actions.</summary>
    Task<LinkExecutionResult> RecordBlockedTriggerAsync(
        string token, string? ipAddress, string? userAgent);

    Task<bool> RevokeLinkAsync(string token);
    Task ExpireOldLinksAsync();

    /// <summary>Brings every active link's home-side trigger up to the current enforcement
    /// model, window and sharing mode, re-arming only the ones that differ.</summary>
    Task<TriggerRearmResult> RearmTriggersAsync(CancellationToken cancellationToken = default);

    /// <summary>Accounts for triggers the home ran while the add-on was not listening: each
    /// missed press is counted and audited, and never executed late.</summary>
    Task<int> ReconcileOfflineTriggersAsync(CancellationToken cancellationToken = default);

    /// <summary>The URL to hand to the recipient: the bot-immune confirm page when a
    /// public URL is configured, otherwise the raw cloudhook URL.</summary>
    string GetShareUrl(TemporaryLink link);
}

public class LinkExecutionResult
{
    public LinkExecutionStatus Status { get; set; }
    public TemporaryLink? Link { get; set; }
    public string? ErrorMessage { get; set; }
}

public enum LinkExecutionStatus
{
    Success,
    NotFound,
    AlreadyUsed,
    NotYetValid,
    Expired,
    Revoked,
    Error,

    /// <summary>The home refused the trigger as outside the validity window. Audited as a
    /// refusal; no use claimed and no actions run.</summary>
    RefusedByHome
}

/// <summary>What one re-arming pass over the active links did.</summary>
public record TriggerRearmResult(int Checked, int Rearmed, int Failed);
