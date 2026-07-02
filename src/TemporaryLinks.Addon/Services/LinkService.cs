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
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        string? recipientPhoneNumber,
        string? customMessage,
        string createdBy,
        string actions,
        int maxUses = 1)
    {
        var token = _tokenGenerator.GenerateSecureToken();

        string webhookId = await _haService.CreateWebhookAutomationAsync(
            token, name, actions, validFrom, validUntil);

        CloudhookResult cloudhook;
        try
        {
            cloudhook = await _haService.CreateCloudhookAsync(webhookId);
        }
        catch
        {
            // Compensate: never leave a live automation in HA with no owning link.
            try
            {
                await _haService.DeleteWebhookAutomationAsync(webhookId);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogError(cleanupEx,
                    "Failed to clean up automation {WebhookId} after cloudhook failure", webhookId);
            }
            throw;
        }

        var link = new TemporaryLink
        {
            Token = token,
            Name = name,
            Actions = actions,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            MaxUses = maxUses,
            UsageCount = 0,
            RecipientPhoneNumber = recipientPhoneNumber,
            CustomMessage = customMessage,
            CreatedBy = createdBy,
            Status = LinkStatus.Active,
            CloudhookId = cloudhook.CloudhookId,
            CloudhookUrl = cloudhook.CloudhookUrl,
            WebhookId = webhookId,
        };

        _context.TemporaryLinks.Add(link);
        await _context.SaveChangesAsync();

        await AddAuditEntryAsync(link.Id, "Created",
            $"Link created by {createdBy} (max uses: {maxUses})");

        return link;
    }

    public async Task<TemporaryLink> UpdateLinkAsync(
        Guid id,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil,
        string? recipientPhoneNumber,
        string? customMessage,
        int maxUses)
    {
        var link = await _context.TemporaryLinks.FindAsync(id);

        if (link == null)
        {
            throw new InvalidOperationException($"Link with ID {id} not found");
        }

        if (link.Status != LinkStatus.Active)
        {
            throw new InvalidOperationException("Only active links can be edited");
        }

        // Don't allow reducing max uses below current usage count
        if (maxUses < link.UsageCount)
        {
            throw new InvalidOperationException($"Max uses cannot be less than current usage count ({link.UsageCount})");
        }

        // Re-arm the home-side window guard BEFORE saving: the automation's condition
        // must reflect the new window, or HA would keep enforcing the old one.
        await _haService.CreateWebhookAutomationAsync(
            link.Token, link.Name, link.Actions, validFrom, validUntil);

        link.ValidFrom = validFrom;
        link.ValidUntil = validUntil;
        link.RecipientPhoneNumber = recipientPhoneNumber;
        link.CustomMessage = customMessage;
        link.MaxUses = maxUses;

        await _context.SaveChangesAsync();

        await AddAuditEntryAsync(link.Id, "Updated",
            $"Link updated: ValidFrom={validFrom:g}, ValidUntil={validUntil:g}, MaxUses={maxUses}, Phone={recipientPhoneNumber}");

        return link;
    }

    public async Task SendSmsAsync(TemporaryLink link)
    {
        if (string.IsNullOrWhiteSpace(link.RecipientPhoneNumber))
        {
            throw new InvalidOperationException(
                "This link has no recipient phone number — add one (Edit) to enable SMS.");
        }

        var message = FormatMessage(link);
        var result = await _twilioService.SendSmsAsync(link.RecipientPhoneNumber, message);

        if (!result.Success || result.MessageSid == null)
        {
            await AddAuditEntryAsync(link.Id, "SmsFailure",
                $"Failed to send SMS: {result.ErrorMessage}", success: false,
                errorMessage: result.ErrorMessage);

            throw new InvalidOperationException($"Failed to send SMS: {result.ErrorMessage}");
        }

        var audit = new LinkSmsAudit()
        {
            TemporaryLinkId = link.Id,
            Content = message,
            TwilioMessageSid = result.MessageSid,
            SmsSent = true,
        };

        _context.LinkSmsAudits.Add(audit);
        await _context.SaveChangesAsync();

        await AddAuditEntryAsync(link.Id, "SmsSent",
            $"SMS sent to {link.RecipientPhoneNumber}");
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

        // Check if max uses reached
        if (link.Status == LinkStatus.Used || link.UsageCount >= link.MaxUses)
        {
            await AddAuditEntryAsync(link.Id, "ExecutionAttempt",
                $"Attempted to use exhausted link (used {link.UsageCount}/{link.MaxUses})", ipAddress, userAgent,
                false);
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
            var wasActive = link.Status != LinkStatus.Expired;
            link.Status = LinkStatus.Expired;
            await _context.SaveChangesAsync();
            await AddAuditEntryAsync(link.Id, "ExecutionAttempt",
                "Attempted to use expired link", ipAddress, userAgent, false);

            // Lazy expiry must clean up like the sweep does — the sweep only scans
            // Active links, so an automation not deleted here would leak forever.
            if (wasActive && !string.IsNullOrEmpty(link.WebhookId))
            {
                try
                {
                    await _haService.DeleteWebhookAutomationAsync(link.WebhookId);
                    await AddAuditEntryAsync(link.Id, "WebhookDeleted",
                        "Webhook automation deleted (link expired at execution time)");
                }
                catch (Exception ex)
                {
                    await AddAuditEntryAsync(link.Id, "ExecutionException",
                        $"Webhook automation delete failed (link expired at execution time) {ex.Message}");
                }
            }

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

        link.UsageCount++;
        await _context.SaveChangesAsync();
        
        await AddAuditEntryAsync(link.Id, "Executed",
            $"Link executed ({link.UsageCount}/{link.MaxUses})",
            ipAddress, userAgent, true);

        // Mark as used and cleanup webhook when max uses is reached
        if (link.UsageCount >= link.MaxUses)
        {
            link.Status = LinkStatus.Used;
            await _context.SaveChangesAsync();

            try
            {
                await _haService.DeleteWebhookAutomationAsync(link.WebhookId);
                await AddAuditEntryAsync(link.Id, "WebhookDeleted",
                    "Webhook automation deleted (max uses reached)");
            }
            catch (Exception ex)
            {
                await AddAuditEntryAsync(link.Id, "ExecutionException",
                    $"Webhook automation deleted failed to delet (max uses reached) {ex.Message}");

                return new LinkExecutionResult
                {
                    Status = LinkExecutionStatus.Error,
                    Link = link,
                    ErrorMessage = "An error occurred while executing the action"
                };
            }
        }

        return new LinkExecutionResult
        {
            Status = LinkExecutionStatus.Success,
            Link = link
        };
    }

    public async Task<TemporaryLink?> GetLinkByIdAsync(Guid id)
    {
        return await _context.TemporaryLinks
            .Include(l => l.AuditEntries)
            .Include(l => l.SmsEntries)
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

        // Delete the webhook automation (cloudhook is auto-deleted by HA). A home-side
        // failure must not undo the revocation — the status is already saved.
        if (!string.IsNullOrEmpty(link.WebhookId))
        {
            try
            {
                await _haService.DeleteWebhookAutomationAsync(link.WebhookId);
                await AddAuditEntryAsync(link.Id, "WebhookDeleted",
                    "Webhook automation deleted (link revoked)");
            }
            catch (Exception ex)
            {
                await AddAuditEntryAsync(link.Id, "ExecutionException",
                    $"Webhook automation delete failed (link revoked) {ex.Message}");
            }
        }

        await AddAuditEntryAsync(link.Id, "Revoked", "Link was revoked");

        return true;
    }

    public async Task ExpireOldLinksAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredLinks = await _context.TemporaryLinks
            .Where(l => l.Status == LinkStatus.Active)
            .Where(l => l.ValidUntil < now)
            .ToListAsync();

        foreach (var link in expiredLinks)
        {
            try
            {
                await _haService.DeleteWebhookAutomationAsync(link.WebhookId);
                await AddAuditEntryAsync(link.Id, "WebhookDeleted",
                    "Webhook automation deleted (Link validity period ended)");
            }
            catch (Exception ex)
            {
                await AddAuditEntryAsync(link.Id, "ExecutionException",
                    $"Webhook automation deleted failed to delete (Link validity period ended) {ex.Message}");
            }
            
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
        Guid linkId,
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

    public string GetShareUrl(TemporaryLink link)
    {
        // The trigger URL rides ONLY in the fragment — never sent to the page's host.
        if (!string.IsNullOrWhiteSpace(_config.SharePageUrl))
        {
            return $"{_config.SharePageUrl.TrimEnd('/')}#{Uri.EscapeDataString(link.CloudhookUrl)}";
        }

        if (string.IsNullOrWhiteSpace(_config.PublicUrl))
        {
            return link.CloudhookUrl;
        }

        return $"{_config.PublicUrl.TrimEnd('/')}/local/{SharePage.RelativePath}" +
               $"#{Uri.EscapeDataString(link.CloudhookUrl)}";
    }

    private string FormatMessage(TemporaryLink link)
    {
        var template = link.CustomMessage ?? _config.DefaultMessageTemplate;

        return template
            .Replace("{link}", GetShareUrl(link))
            .Replace("{start_time}", link.ValidFrom.ToLocalTime().ToString("g"))
            .Replace("{end_time}", link.ValidUntil.ToLocalTime().ToString("g"))
            .Replace("{name}", link.Name);
    }
}