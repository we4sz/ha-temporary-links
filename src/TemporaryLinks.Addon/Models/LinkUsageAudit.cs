namespace TemporaryLinks.Addon.Models;

public class LinkUsageAudit
{
    public Guid Id { get; set; }
    public Guid TemporaryLinkId { get; set; }
    public TemporaryLink TemporaryLink { get; set; } = null!;
    public required string EventType { get; set; }
    public required string Description { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
