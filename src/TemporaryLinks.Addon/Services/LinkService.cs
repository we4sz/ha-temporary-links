using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TemporaryLinks.Addon.Configuration;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Models;

namespace TemporaryLinks.Addon.Services;

public class LinkService : ILinkService
{
    private readonly ApplicationDbContext _context;
    private readonly ITokenGenerator _tokenGenerator;
    private readonly ITwilioService _twilioService;
    private readonly IHomeAssistantService _haService;
    private readonly AddonConfiguration _config;
    private readonly ILogger<LinkService> _logger;

    public LinkService(
        ApplicationDbContext context,
        ITokenGenerator tokenGenerator,
        ITwilioService twilioService,
        IHomeAssistantService haService,
        IOptions<AddonConfiguration> config,
        ILogger<LinkService> logger)
    {
        _context = context;
        _tokenGenerator = tokenGenerator;
        _twilioService = twilioService;
        _haService = haService;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<TemporaryLink> CreateLinkAsync(
        string name,
        string scriptEntityId,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        string? recipientPhoneNumber,
        string? customMessage,
        string? scriptData,
        string createdBy,
        string baseUrl,
        bool sendSmsImmediately = true)
    {
        var token = _tokenGenerator.GenerateSecureToken();

        var link = new TemporaryLink
        {
            Token = token,
            Name = name,
            ScriptEntityId = scriptEntityId,
            ScriptData = scriptData,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            RecipientPhoneNumber = recipientPhoneNumber,
            CustomMessage = customMessage,
            CreatedBy = createdBy,
            Status = LinkStatus.Pending
        };

        _context.TemporaryLinks.Add(link);
        await _context.SaveChangesAsync();

        await AddAuditEntryAsync(link.Id, "Created",
            $"Link created by {createdBy} for script {scriptEntityId}");

        if (sendSmsImmediately && !string.IsNullOrWhiteSpace(recipientPhoneNumber) && _twilioService.IsConfigured)
        {
            var linkUrl = $"{baseUrl.TrimEnd('/')}/link/{token}";
            var message = FormatMessage(link, linkUrl, customMessage);
            var result = await _twilioService.SendSmsAsync(recipientPhoneNumber, message);

            link.SmsSent = result.Success;
            link.TwilioMessageSid = result.MessageSid;

            if (result.Success)
            {
                link.Status = LinkStatus.Active;
                await AddAuditEntryAsync(link.Id, "SmsSent",
                    $"SMS sent to {recipientPhoneNumber}");
            }
            else
            {
                await AddAuditEntryAsync(link.Id, "SmsFailure",
                    $"Failed to send SMS: {result.ErrorMessage}", success: false,
                    errorMessage: result.ErrorMessage);
            }

            await _context.SaveChangesAsync();
        }
        else if (string.IsNullOrWhiteSpace(recipientPhoneNumber))
        {
            link.Status = LinkStatus.Active;
            await _context.SaveChangesAsync();
        }

        return link;
    }

    public async Task<LinkExecutionResult> ExecuteLinkAsync(
        string token, string? ipAddress, string? userAgent)
    {
        var link = await _context.TemporaryLinks
            .FirstOrDefaultAsync(l => l.Token == token);

        if (link == null)
        {
            _logger.LogWarning("Link not found: {Token}", token);
            return new LinkExecutionResult { Status = LinkExecutionStatus.NotFound };
        }

        var now = DateTimeOffset.UtcNow;

        if (link.Status == LinkStatus.Used)
        {
            await AddAuditEntryAsync(link.Id, "ExecutionAttempt",
                "Attempted to use already-used link", ipAddress, userAgent, false);
            return new LinkExecutionResult
            {
                Status = LinkExecutionStatus.AlreadyUsed,
                Link = link
            };
        }

        if (link.Status == LinkStatus.Revoked)
        {
            await AddAuditEntryAsync(link.Id, "ExecutionAttempt",
                "Attempted to use revoked link", ipAddress, userAgent, false);
            return new LinkExecutionResult { Status = LinkExecutionStatus.Revoked };
        }

        if (link.Status == LinkStatus.Expired || now > link.ValidUntil)
        {
            link.Status = LinkStatus.Expired;
            await _context.SaveChangesAsync();
            await AddAuditEntryAsync(link.Id, "ExecutionAttempt",
                "Attempted to use expired link", ipAddress, userAgent, false);
            return new LinkExecutionResult
            {
                Status = LinkExecutionStatus.Expired,
                Link = link
            };
        }

        if (now < link.ValidFrom)
        {
            await AddAuditEntryAsync(link.Id, "ExecutionAttempt",
                $"Attempted to use link before valid period (valid from {link.ValidFrom})",
                ipAddress, userAgent, false);
            return new LinkExecutionResult
            {
                Status = LinkExecutionStatus.NotYetValid,
                Link = link
            };
        }

        try
        {
            var success = await _haService.CallScriptAsync(link.ScriptEntityId, link.ScriptData);

            if (success)
            {
                link.Status = LinkStatus.Used;
                link.UsedAt = now;
                link.UsedByIpAddress = ipAddress;
                await _context.SaveChangesAsync();

                await AddAuditEntryAsync(link.Id, "Executed",
                    $"Link successfully executed, script {link.ScriptEntityId} called",
                    ipAddress, userAgent, true);

                return new LinkExecutionResult
                {
                    Status = LinkExecutionStatus.Success,
                    Link = link
                };
            }
            else
            {
                await AddAuditEntryAsync(link.Id, "ExecutionFailure",
                    "Failed to call Home Assistant script",
                    ipAddress, userAgent, false, "Script call returned failure");

                return new LinkExecutionResult
                {
                    Status = LinkExecutionStatus.Error,
                    Link = link,
                    ErrorMessage = "Failed to execute the action"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception executing link {Token}", token);
            await AddAuditEntryAsync(link.Id, "ExecutionException",
                "Exception during link execution",
                ipAddress, userAgent, false, ex.Message);

            return new LinkExecutionResult
            {
                Status = LinkExecutionStatus.Error,
                Link = link,
                ErrorMessage = "An error occurred while executing the action"
            };
        }
    }

    public async Task<TemporaryLink?> GetLinkByTokenAsync(string token)
    {
        return await _context.TemporaryLinks
            .Include(l => l.AuditEntries)
            .FirstOrDefaultAsync(l => l.Token == token);
    }

    public async Task<TemporaryLink?> GetLinkByIdAsync(int id)
    {
        return await _context.TemporaryLinks
            .Include(l => l.AuditEntries)
            .FirstOrDefaultAsync(l => l.Id == id);
    }

    public async Task<IList<TemporaryLink>> GetLinksAsync(string? statusFilter = null)
    {
        var query = _context.TemporaryLinks.AsQueryable();

        if (!string.IsNullOrWhiteSpace(statusFilter) &&
            Enum.TryParse<LinkStatus>(statusFilter, true, out var status))
        {
            query = query.Where(l => l.Status == status);
        }

        return await query
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();
    }

    public async Task<bool> RevokeLinkAsync(string token)
    {
        var link = await _context.TemporaryLinks
            .FirstOrDefaultAsync(l => l.Token == token);

        if (link == null)
            return false;

        if (link.Status == LinkStatus.Used)
            return false;

        link.Status = LinkStatus.Revoked;
        await _context.SaveChangesAsync();

        await AddAuditEntryAsync(link.Id, "Revoked", "Link was revoked");

        return true;
    }

    public async Task ExpireOldLinksAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredLinks = await _context.TemporaryLinks
            .Where(l => l.Status == LinkStatus.Active || l.Status == LinkStatus.Pending)
            .Where(l => l.ValidUntil < now)
            .ToListAsync();

        foreach (var link in expiredLinks)
        {
            link.Status = LinkStatus.Expired;
            await AddAuditEntryAsync(link.Id, "Expired", "Link validity period ended");
        }

        await _context.SaveChangesAsync();

        if (expiredLinks.Count > 0)
        {
            _logger.LogInformation("Expired {Count} links", expiredLinks.Count);
        }
    }

    private async Task AddAuditEntryAsync(
        int linkId,
        string eventType,
        string description,
        string? ipAddress = null,
        string? userAgent = null,
        bool success = true,
        string? errorMessage = null)
    {
        var audit = new LinkUsageAudit
        {
            TemporaryLinkId = linkId,
            EventType = eventType,
            Description = description,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Success = success,
            ErrorMessage = errorMessage
        };

        _context.LinkUsageAudits.Add(audit);
        await _context.SaveChangesAsync();
    }

    private string FormatMessage(TemporaryLink link, string linkUrl, string? customMessage)
    {
        var template = customMessage ?? _config.DefaultMessageTemplate;

        return template
            .Replace("{link}", linkUrl)
            .Replace("{start_time}", link.ValidFrom.ToLocalTime().ToString("g"))
            .Replace("{end_time}", link.ValidUntil.ToLocalTime().ToString("g"))
            .Replace("{name}", link.Name);
    }
}
