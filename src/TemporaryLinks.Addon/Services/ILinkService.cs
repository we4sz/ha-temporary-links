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
    Task<LinkExecutionResult> ExecuteLinkAsync(string token, string? ipAddress, string? userAgent);
    Task<bool> RevokeLinkAsync(string token);
    Task ExpireOldLinksAsync();

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
    Error
}
