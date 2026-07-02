namespace TemporaryLinks.Addon.Models;

public class TemporaryLink
{
    public Guid Id { get; set; }
    public required string Token { get; set; }
    public required string Name { get; set; }
    public required string Actions { get; set; }  // JSON array of automation actions
    public required DateTimeOffset ValidFrom { get; set; }
    public required DateTimeOffset ValidUntil { get; set; }
    public int MaxUses { get; set; } = 1;
    public int UsageCount { get; set; } = 0;
    public required string WebhookId { get; set; }
    public required string CloudhookId { get; set; }
    public required string CloudhookUrl { get; set; }
    public string? RecipientPhoneNumber { get; set; }
    public string? CustomMessage { get; set; }
    public required LinkStatus Status { get; set; } 
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public required string CreatedBy { get; set; }
    public ICollection<LinkUsageAudit> AuditEntries { get; set; } = [];
    public ICollection<LinkSmsAudit> SmsEntries { get; set; } = [];
}
