namespace TemporaryLinks.Addon.Services;

public interface ITwilioService
{
    Task<TwilioSendResult> SendSmsAsync(
        string toPhoneNumber,
        string message,
        CancellationToken cancellationToken = default);

    Task<bool> ValidateConfigurationAsync();

    bool IsConfigured { get; }
}

public class TwilioSendResult
{
    public bool Success { get; set; }
    public string? MessageSid { get; set; }
    public string? ErrorMessage { get; set; }
}
