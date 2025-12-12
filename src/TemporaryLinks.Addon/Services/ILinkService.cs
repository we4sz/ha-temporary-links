using TemporaryLinks.Addon.Models;

namespace TemporaryLinks.Addon.Services;

public interface ILinkService
{
    Task<TemporaryLink> CreateLinkAsync(
        string name,
        string scriptEntityId,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        string? recipientPhoneNumber,
        string? customMessage,
        string? scriptData,
        string createdBy,
        string baseUrl,
        bool sendSmsImmediately = true,
        int maxUses = 1);

    Task<string> GetLinkUrlAsync(TemporaryLink link, string fallbackBaseUrl);
    Task<TemporaryLink?> GetLinkByTokenAsync(string token);
    Task<TemporaryLink?> GetLinkByIdAsync(int id);
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
