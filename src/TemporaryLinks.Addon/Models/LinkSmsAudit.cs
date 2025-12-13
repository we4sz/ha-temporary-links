namespace TemporaryLinks.Addon.Models;

public class LinkSmsAudit
{
    public Guid Id { get; set; }
    
    public Guid TemporaryLinkId { get; set; }
    
    public TemporaryLink TemporaryLink { get; set; } = null!;
    
    public required string TwilioMessageSid { get; set; }
    
    public required string Content { get; set; }
    
    public bool SmsSent { get; set; }
    
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
