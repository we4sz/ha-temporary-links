namespace TemporaryLinks.Addon.Models;

public class ActionTemplate
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string Actions { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedAt { get; set; }
}
