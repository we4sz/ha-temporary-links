namespace TemporaryLinks.Addon.Configuration;

public class AddonConfiguration
{
    public const string SectionName = "HomeAssistant";

    public string BaseUri { get; set; } = "http://supervisor/core/api/";
    public string? Token { get; set; }
    public string? HaUrl { get; set; }
    public string? HaToken { get; set; }
    public string DefaultMessageTemplate { get; set; } =
        "Your temporary access link: {link}\nValid from {start_time} to {end_time}";
}
