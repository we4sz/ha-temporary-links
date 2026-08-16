using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TemporaryLinks.Addon.Configuration;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Models;

namespace TemporaryLinks.Addon.Services;


public class LinkService : ILinkService
{
    /// <summary>How far the home's clock may run ahead of the add-on's before a trigger the
    /// add-on already processed would look like one it missed. Reconciliation only counts a
    /// press that is later than the watermark by more than this.</summary>
    private static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromSeconds(5);

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
        // A link is shared through the confirm page and nothing else, so an installation with
        // no page to share cannot issue one at all: refuse here, before anything exists in the
        // home, rather than handing out a link nobody can press (E2.S6.A2 / E7.S2.A1).
        RequireConfirmPageHosting();

        // Bring the actions to exactly the contract execution enforces BEFORE anything is
        // created in the home: a link accepted here can never later fail on their form.
        var normalizedActions = ActionsNormalizer.Normalize(actions);

        var token = _tokenGenerator.GenerateSecureToken();

        string webhookId = await _haService.CreateWebhookAutomationAsync(
            token, name, normalizedActions, validFrom, validUntil);

        CloudhookResult cloudhook;
        try
        {
            cloudhook = await _haService.CreateCloudhookAsync(webhookId);
        }
        catch
        {
            // Compensate: never leave a live automation in HA with no owning link.
            await CompensateCreationAsync(webhookId, cloudhookExists: false);
            throw;
        }

        var link = new TemporaryLink
        {
            Token = token,
            Name = name,
            Actions = normalizedActions,
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
            TriggerAcceptsPost = true,
            LastTriggerProcessedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            _context.TemporaryLinks.Add(link);
            await _context.SaveChangesAsync();
        }
        catch
        {
            // The trigger and its public relay exist but nothing owns them — take both back
            // out of the home before surfacing the failure (E1.S1.A4).
            _context.Entry(link).State = EntityState.Detached;
            await CompensateCreationAsync(webhookId, cloudhookExists: true);
            throw;
        }

        await AddAuditEntryAsync(link.Id, "Created",
            $"Link created by {createdBy} (max uses: {maxUses})");

        return link;
    }

    /// <summary>The confirm page is the only way a link is shared, so one must be reachable
    /// before a link is worth creating: a shared page, or the home's own public URL to serve
    /// it from. With neither, creation is refused with what to enable — never quietly
    /// downgraded to a link a preview bot could consume.</summary>
    private void RequireConfirmPageHosting()
    {
        if (!string.IsNullOrWhiteSpace(_config.SharePageUrl) ||
            !string.IsNullOrWhiteSpace(_config.PublicUrl))
        {
            return;
        }

        throw new InvalidOperationException(
            "No link was created: links are shared through a confirm page, and this " +
            "installation has none. Set the add-on option share_page_url (the hosted default " +
            "works as-is), or turn on Home Assistant Cloud remote access — or set public_url " +
            "for your own domain — so the add-on can serve the page itself.");
    }

    /// <summary>Takes back everything a failed creation already put in the home. Both steps
    /// are best-effort: a cleanup failure is logged, never allowed to mask the real error.</summary>
    private async Task CompensateCreationAsync(string webhookId, bool cloudhookExists)
    {
        if (cloudhookExists)
        {
            try
            {
                await _haService.DeleteCloudhookAsync(webhookId);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogError(cleanupEx,
                    "Failed to clean up cloudhook {WebhookId} after a failed creation", webhookId);
            }
        }

        try
        {
            await _haService.DeleteWebhookAutomationAsync(webhookId);
        }
        catch (Exception cleanupEx)
        {
            _logger.LogError(cleanupEx,
                "Failed to clean up automation {WebhookId} after a failed creation", webhookId);
        }
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
        link.TriggerAcceptsPost = true;

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

    public Task<LinkExecutionResult> ExecuteLinkAsync(
        string token, string? ipAddress, string? userAgent) =>
        JudgeTriggerAsync(token, ipAddress, userAgent, refusedByHome: false);

    public Task<LinkExecutionResult> RecordBlockedTriggerAsync(
        string token, string? ipAddress, string? userAgent) =>
        JudgeTriggerAsync(token, ipAddress, userAgent, refusedByHome: true);

    /// <summary>
    /// Judges one presented token in the fixed order of scrutiny (unknown, exhausted, revoked,
    /// expired, not-yet-valid, then success) and records the verdict.
    ///
    /// <paramref name="refusedByHome"/> distinguishes the two things the home can announce.
    /// A trigger the home already refused (outside the window as the HOME sees it) is audited
    /// only: it never claims a use and never runs actions, even when the add-on's own clock
    /// would have called the link in-window — the home's verdict on its own window stands.
    /// </summary>
    private async Task<LinkExecutionResult> JudgeTriggerAsync(
        string token, string? ipAddress, string? userAgent, bool refusedByHome)
    {
        var link = await _context.TemporaryLinks
            .FirstOrDefaultAsync(l => l.Token == token);

        if (link == null)
        {
            _logger.LogWarning("Trigger received for an unknown link token");
            return new LinkExecutionResult { Status = LinkExecutionStatus.NotFound };
        }

        // The home ran this link's automation, so its last_triggered has just moved. Record
        // that we saw it, or reconciliation would later mistake it for a press we missed.
        link.LastTriggerProcessedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;

        // Fast path for an already-retired link (nice audit message). The allowance itself
        // is not checked here — the atomic claim below is the single source of truth, so a
        // link that is at its allowance but still marked Active is caught there, not here.
        if (link.Status == LinkStatus.Used)
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
            return new LinkExecutionResult { Status = LinkExecutionStatus.Revoked, Link = link };
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
            if (wasActive)
            {
                await TryRemoveTriggerAsync(link, "link expired at execution time");
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

        if (refusedByHome)
        {
            // The home says outside the window; the add-on's clock says inside. The home owns
            // the trigger, so nothing ran — audit the refusal and claim nothing.
            await AddAuditEntryAsync(link.Id, "ExecutionAttempt",
                "Trigger refused by the home as outside the validity window — no use claimed " +
                "and no actions run",
                ipAddress, userAgent, false);
            return new LinkExecutionResult
            {
                Status = LinkExecutionStatus.RefusedByHome,
                Link = link
            };
        }

        // Atomically claim one use: the conditional UPDATE only succeeds if the link is
        // still active with an unspent allowance, so two triggers milliseconds apart can
        // never both claim the last slot (E7.S1.A2 / E7.S3.A1) — independent of how many
        // threads process events.
        var claimed = await ClaimOneUseAsync(link.Id);

        // The authoritative count lives in the row, never in this instance: re-read it rather
        // than incrementing here, or a later save would write a stale absolute value back over
        // a count another handler has since moved (E7.S3.A1).
        await _context.Entry(link).ReloadAsync();

        if (claimed == 0)
        {
            // The allowance is spent — another trigger took the last use, or it was edited
            // down to the count. Either way this link is done: retire it like any exhaustion.
            await AddAuditEntryAsync(link.Id, "ExecutionAttempt",
                $"Attempted to use exhausted link (used {link.UsageCount}/{link.MaxUses})",
                ipAddress, userAgent, false);
            await RetireAsync(link, "allowance already spent");
            return new LinkExecutionResult { Status = LinkExecutionStatus.AlreadyUsed, Link = link };
        }

        // Only now — after the use is claimed — does the add-on run the link's real actions
        // (E7.S1.A1). If the home refuses them, the use still counts, so a failing action
        // cannot be retried to bypass the allowance.
        try
        {
            await _haService.ExecuteActionsAsync(link.Actions);
        }
        catch (Exception ex)
        {
            await AddAuditEntryAsync(link.Id, "ExecutionException",
                $"Actions failed to run ({link.UsageCount}/{link.MaxUses}): {ex.Message}",
                ipAddress, userAgent, false);
            await RetireIfExhaustedAsync(link);
            return new LinkExecutionResult
            {
                Status = LinkExecutionStatus.Error,
                Link = link,
                ErrorMessage = "An error occurred while running the link's actions."
            };
        }

        await AddAuditEntryAsync(link.Id, "Executed",
            $"Link executed ({link.UsageCount}/{link.MaxUses})",
            ipAddress, userAgent, true);

        // The execution's own outcome is the verdict: a trigger left standing because the home
        // refused the removal is a cleanup problem (audited, and retried by the sweep), not a
        // failed use (E2.S3.A2).
        await RetireIfExhaustedAsync(link);

        return new LinkExecutionResult
        {
            Status = LinkExecutionStatus.Success,
            Link = link
        };
    }

    /// <summary>The single atomic claim: one use, only while the link is active with an
    /// unspent allowance. Returns the number of rows claimed (0 or 1).</summary>
    private Task<int> ClaimOneUseAsync(Guid linkId) =>
        _context.TemporaryLinks
            .Where(l => l.Id == linkId
                        && l.Status == LinkStatus.Active
                        && l.UsageCount < l.MaxUses)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.UsageCount, l => l.UsageCount + 1));

    /// <summary>Retires a link whose allowance is now spent.</summary>
    private async Task RetireIfExhaustedAsync(TemporaryLink link)
    {
        if (link.UsageCount < link.MaxUses)
        {
            return;
        }

        await RetireAsync(link, "max uses reached");
    }

    /// <summary>Marks a link used and takes its trigger out of the home.</summary>
    private async Task RetireAsync(TemporaryLink link, string reason)
    {
        if (link.Status == LinkStatus.Active)
        {
            link.Status = LinkStatus.Used;
            await _context.SaveChangesAsync();
        }

        await TryRemoveTriggerAsync(link, reason);
    }

    /// <summary>
    /// The one way a link's trigger leaves the home. On confirmed removal the link's trigger id
    /// is cleared — a still-set id on a dead link is the marker that the trigger is STILL
    /// STANDING, which the sweep retries (E1.S4.A5). A failure is audited distinctly and never
    /// changes the verdict of whatever was being done.
    /// </summary>
    /// <param name="auditFailure">False on a retry, where the failure is already on the record:
    /// the retry audits only when the removal finally lands, so a home that stays unreachable
    /// cannot fill the audit trail with one entry per link per sweep.</param>
    /// <returns>true when the home no longer hosts the trigger.</returns>
    private async Task<bool> TryRemoveTriggerAsync(
        TemporaryLink link, string reason, bool auditFailure = true)
    {
        if (string.IsNullOrEmpty(link.WebhookId))
        {
            return true;
        }

        try
        {
            var removed = await _haService.DeleteWebhookAutomationAsync(link.WebhookId);

            link.WebhookId = string.Empty;
            await _context.SaveChangesAsync();

            if (removed)
            {
                await AddAuditEntryAsync(link.Id, "WebhookDeleted",
                    $"Webhook automation deleted ({reason})");
            }

            return true;
        }
        catch (Exception ex)
        {
            // Leave WebhookId set: the trigger may still be standing, and the sweep retries
            // every dead link that still carries one.
            if (auditFailure)
            {
                await AddAuditEntryAsync(link.Id, "WebhookDeleteFailed",
                    $"Webhook automation delete failed ({reason}): {ex.Message}",
                    success: false, errorMessage: ex.Message);
            }
            else
            {
                _logger.LogWarning(ex,
                    "Retrying the trigger removal for link {LinkId} failed again", link.Id);
            }
            return false;
        }
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
        await TryRemoveTriggerAsync(link, "link revoked");

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
            link.Status = LinkStatus.Expired;
            await _context.SaveChangesAsync();
            await AddAuditEntryAsync(link.Id, "Expired", "Link validity period ended");
            await TryRemoveTriggerAsync(link, "link validity period ended");
        }

        if (expiredLinks.Count > 0)
        {
            _logger.LogInformation("Expired {Count} links", expiredLinks.Count);
        }

        // A dead link whose trigger is still standing (an earlier removal the home refused)
        // gets another attempt every pass, until the home confirms it is gone (E1.S4.A5).
        var justExpired = expiredLinks.Select(l => l.Id).ToHashSet();
        var stranded = await _context.TemporaryLinks
            .Where(l => l.Status != LinkStatus.Active && l.WebhookId != "")
            .ToListAsync();

        foreach (var link in stranded.Where(l => !justExpired.Contains(l.Id)))
        {
            await TryRemoveTriggerAsync(
                link, "retry after an earlier removal failure", auditFailure: false);
        }
    }

    public async Task<TriggerRearmResult> RearmTriggersAsync(CancellationToken cancellationToken = default)
    {
        var links = await _context.TemporaryLinks
            .Where(l => l.Status == LinkStatus.Active)
            .ToListAsync(cancellationToken);

        var rearmed = 0;
        var failed = 0;

        foreach (var link in links)
        {
            if (string.IsNullOrEmpty(link.WebhookId))
            {
                continue;
            }

            try
            {
                var stored = await _haService.TryGetAutomationConfigAsync(
                    link.WebhookId, cancellationToken);

                // Already the current model, window and gesture — leave it alone, so a boot
                // costs no audit noise.
                if (stored is not null &&
                    link.TriggerAcceptsPost == true &&
                    AutomationModel.MatchesCurrentModel(
                        stored.Value,
                        link.WebhookId,
                        AutomationModel.WindowTemplate(link.ValidFrom, link.ValidUntil)))
                {
                    continue;
                }

                await _haService.CreateWebhookAutomationAsync(
                    link.Token, link.Name, link.Actions,
                    link.ValidFrom, link.ValidUntil, cancellationToken);

                link.TriggerAcceptsPost = true;
                await _context.SaveChangesAsync(cancellationToken);

                await AddAuditEntryAsync(link.Id, "TriggerRearmed",
                    "Trigger re-armed to current enforcement model" +
                    (stored is null ? " (the home had no trigger for this link)" : ""));
                rearmed++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex,
                    "Could not re-arm the trigger for link {LinkId} — will retry", link.Id);
            }
        }

        return new TriggerRearmResult(links.Count, rearmed, failed);
    }

    public async Task<int> ReconcileOfflineTriggersAsync(CancellationToken cancellationToken = default)
    {
        var lastTriggered = await _haService.GetAutomationLastTriggeredAsync(cancellationToken);

        var links = await _context.TemporaryLinks
            .Where(l => l.Status == LinkStatus.Active)
            .ToListAsync(cancellationToken);

        var reconciled = 0;

        foreach (var link in links)
        {
            if (string.IsNullOrEmpty(link.WebhookId) ||
                !lastTriggered.TryGetValue(link.WebhookId, out var firedAt))
            {
                continue;
            }

            // No watermark at all (a link from before the add-on kept one): adopt what the
            // home reports rather than counting a press that may long since be accounted for.
            if (link.LastTriggerProcessedAt is not { } watermark)
            {
                link.LastTriggerProcessedAt = firedAt;
                await _context.SaveChangesAsync(cancellationToken);
                continue;
            }

            if (firedAt <= watermark + ClockSkewTolerance)
            {
                continue;
            }

            // Move the watermark first: this press is now accounted for either way, and the
            // count must never be claimed twice for it.
            link.LastTriggerProcessedAt = firedAt;
            await _context.SaveChangesAsync(cancellationToken);

            if (firedAt < link.ValidFrom || firedAt > link.ValidUntil)
            {
                // The home refused it at the time — nothing ran, nothing is owed.
                await AddAuditEntryAsync(link.Id, "ExecutionAttempt",
                    "Trigger fired while the add-on was offline, outside the validity window — " +
                    "refused by the home, no use counted",
                    success: false);
                continue;
            }

            var claimed = await ClaimOneUseAsync(link.Id);
            await _context.Entry(link).ReloadAsync(cancellationToken);

            if (claimed == 0)
            {
                await AddAuditEntryAsync(link.Id, "ExecutionAttempt",
                    $"Trigger fired while the add-on was offline with no allowance left " +
                    $"(used {link.UsageCount}/{link.MaxUses}) — actions were not executed",
                    success: false);
                await RetireAsync(link, "allowance already spent");
                continue;
            }

            // Never late: the actions do not run now. The use is counted and recorded so the
            // allowance stays honest — direct when called, or not at all (E7.S1.A3).
            await AddAuditEntryAsync(link.Id, "OfflineUse",
                $"Link pressed while the add-on was offline — actions were not executed; " +
                $"at least one press counted ({link.UsageCount}/{link.MaxUses})",
                success: false);
            await RetireIfExhaustedAsync(link);
            reconciled++;
        }

        if (reconciled > 0)
        {
            _logger.LogInformation(
                "Reconciled {Count} trigger(s) fired while the add-on was offline", reconciled);
        }

        return reconciled;
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
        // The URL's FORM must match the gesture this link's trigger was ARMED to accept — not
        // the one sharing mode the add-on arms today, or a link issued before the upgrade
        // would be refused by its own trigger (E7.S7.A2). Links armed before that was
        // recorded fall back to the current mode until the boot pass re-arms them.
        var acceptsPost = link.TriggerAcceptsPost ?? true;

        if (!acceptsPost)
        {
            // Issued before the confirm page became the only sharing mode, and not yet
            // re-armed (the home was unreachable at boot): its trigger still answers a plain
            // fetch, and a confirm page would POST at it. The re-arm pass retires this form.
            return link.CloudhookUrl;
        }

        // The trigger URL rides ONLY in the fragment — never sent to the page's host.
        if (!string.IsNullOrWhiteSpace(_config.SharePageUrl))
        {
            return $"{_config.SharePageUrl.TrimEnd('/')}#{Uri.EscapeDataString(link.CloudhookUrl)}";
        }

        if (!string.IsNullOrWhiteSpace(_config.PublicUrl))
        {
            return $"{_config.PublicUrl.TrimEnd('/')}/local/{SharePage.RelativePath}" +
                   $"#{Uri.EscapeDataString(link.CloudhookUrl)}";
        }

        // Armed for the confirm page, but the hosting it was created with has since been
        // removed: the raw trigger is all that is left to show. It is not consumable by a
        // preview bot (the trigger takes POST only) — it is simply unusable until a page is
        // configured again, which is exactly what creation now refuses to start without.
        return link.CloudhookUrl;
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
