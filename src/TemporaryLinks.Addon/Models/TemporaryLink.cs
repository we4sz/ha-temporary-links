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
    /// <summary>The home-side trigger (automation) id while it still stands. Cleared only
    /// when the home has confirmed the trigger is gone, so a non-empty value on a dead link
    /// means "the trigger is still standing and removal must be retried".</summary>
    public required string WebhookId { get; set; }
    public required string CloudhookId { get; set; }
    public required string CloudhookUrl { get; set; }

    /// <summary>The gesture the link's trigger was ARMED to accept: true = an explicit POST
    /// (confirm page), false = a plain GET (one tap). Null for links armed before this was
    /// recorded — those fall back to the current sharing mode until they are re-armed.</summary>
    public bool? TriggerAcceptsPost { get; set; }

    /// <summary>When the add-on last processed a trigger for this link. The watermark the
    /// offline reconciliation compares the home's last_triggered against, so a press the
    /// add-on never saw can be recognised. Null on links created before it was recorded.</summary>
    public DateTimeOffset? LastTriggerProcessedAt { get; set; }
    public string? RecipientPhoneNumber { get; set; }
    public string? CustomMessage { get; set; }
    public required LinkStatus Status { get; set; } 
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public required string CreatedBy { get; set; }
    public ICollection<LinkUsageAudit> AuditEntries { get; set; } = [];
    public ICollection<LinkSmsAudit> SmsEntries { get; set; } = [];
}
