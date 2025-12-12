using Microsoft.Extensions.Options;
using TemporaryLinks.Addon.Configuration;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace TemporaryLinks.Addon.Services;

public class TwilioService : ITwilioService
{
    private readonly TwilioConfiguration _config;
    private readonly ILogger<TwilioService> _logger;
    private readonly bool _isConfigured;

    public TwilioService(IOptions<TwilioConfiguration> config, ILogger<TwilioService> logger)
    {
        _config = config.Value;
        _logger = logger;

        _isConfigured = !string.IsNullOrWhiteSpace(_config.AccountSid)
            && !string.IsNullOrWhiteSpace(_config.AuthToken)
            && !string.IsNullOrWhiteSpace(_config.PhoneNumber);

        if (_isConfigured)
        {
            TwilioClient.Init(_config.AccountSid, _config.AuthToken);
            _logger.LogInformation("Twilio client initialized");
        }
        else
        {
            _logger.LogWarning("Twilio not configured - SMS functionality disabled");
        }
    }

    public bool IsConfigured => _isConfigured;

    public async Task<TwilioSendResult> SendSmsAsync(
        string toPhoneNumber,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (!_isConfigured)
        {
            return new TwilioSendResult
            {
                Success = false,
                ErrorMessage = "Twilio is not configured"
            };
        }

        try
        {
            _logger.LogInformation("Sending SMS to {PhoneNumber}", toPhoneNumber);

            var messageResource = await MessageResource.CreateAsync(
                to: new PhoneNumber(toPhoneNumber),
                from: new PhoneNumber(_config.PhoneNumber),
                body: message);

            _logger.LogInformation("SMS sent successfully. SID: {Sid}", messageResource.Sid);

            return new TwilioSendResult
            {
                Success = true,
                MessageSid = messageResource.Sid
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SMS to {PhoneNumber}", toPhoneNumber);

            return new TwilioSendResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }
}
