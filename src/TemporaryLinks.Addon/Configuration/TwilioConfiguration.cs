namespace TemporaryLinks.Addon.Configuration;

public class TwilioConfiguration
{
    public const string SectionName = "Twilio";

    public string? AccountSid { get; set; }
    public string? AuthToken { get; set; }
    public string? PhoneNumber { get; set; }
}
