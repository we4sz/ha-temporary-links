namespace TemporaryLinks.Addon.Configuration;

public class AddonConfiguration
{
    public const string SectionName = "HomeAssistant";
    public required string HaUrl { get; set; }
    public required string HaToken { get; set; }
    public string DefaultMessageTemplate { get; set; } =
        "Your temporary access link: {link}\nValid from {start_time} to {end_time}";
}
