namespace TemporaryLinks.Addon.Models;

public class Contact
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string PhoneNumber { get; set; }

    public string? Email { get; set; }

    public string? Info { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedAt { get; set; }
}
