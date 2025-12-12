namespace TemporaryLinks.Addon.Models;

public class TemporaryLink
{
    public int Id { get; set; }
    public required string Token { get; set; }
    public required string Name { get; set; }
    public required string ScriptEntityId { get; set; }
    public string? ScriptData { get; set; }
    public required DateTimeOffset ValidFrom { get; set; }
    public required DateTimeOffset ValidUntil { get; set; }
    public int MaxUses { get; set; } = 1;
    public int UsageCount { get; set; } = 0;
    public string? WebhookId { get; set; }
    public string? CloudhookId { get; set; }
    public string? CloudhookUrl { get; set; }
    public string? RecipientPhoneNumber { get; set; }
    public string? CustomMessage { get; set; }
    public LinkStatus Status { get; set; } = LinkStatus.Pending;
    public DateTimeOffset? UsedAt { get; set; }
    public string? UsedByIpAddress { get; set; }
    public string? TwilioMessageSid { get; set; }
    public bool SmsSent { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public required string CreatedBy { get; set; }
    public ICollection<LinkUsageAudit> AuditEntries { get; set; } = [];
}
