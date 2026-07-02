namespace TemporaryLinks.Addon.Configuration;

public class AddonConfiguration
{
    public const string SectionName = "HomeAssistant";
    public required string HaUrl { get; set; }
    public required string HaToken { get; set; }
    public string DefaultMessageTemplate { get; set; } =
        "Your temporary access link: {link}\nValid from {start_time} to {end_time}";

    /// <summary>
    /// Public base URL of the Home Assistant instance (e.g. the Nabu Casa remote URL).
    /// When set, shared links point to the bot-immune confirm page under /local/ and the
    /// trigger webhook only accepts POST. When empty, links are the raw cloudhook URL (GET).
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// URL of a shared, publicly hosted copy of the confirm page (any installation can use
    /// the same one). Takes precedence over self-hosting via PublicUrl. The trigger URL is
    /// carried only in the location fragment, so the shared host never sees it.
    /// </summary>
    public string? SharePageUrl { get; set; }
}
