using TemporaryLinks.Addon.Models;

namespace TemporaryLinks.Addon.Services;

public interface ILinkService
{
    Task<TemporaryLink> CreateLinkAsync(
        string name,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        string recipientPhoneNumber,
        string? customMessage,
        string createdBy,
        string actions,
        int maxUses = 1);


    Task SendSmsAsync(TemporaryLink link);
    Task<TemporaryLink?> GetLinkByIdAsync(Guid id);
    Task<IList<TemporaryLink>> GetLinksAsync(string? statusFilter = null);
    Task<LinkExecutionResult> ExecuteLinkAsync(string token, string? ipAddress, string? userAgent);
    Task<bool> RevokeLinkAsync(string token);
    Task ExpireOldLinksAsync();
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
