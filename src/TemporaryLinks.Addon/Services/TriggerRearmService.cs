namespace TemporaryLinks.Addon.Services;

/// <summary>
/// Brings the home's triggers up to date once per start, in the background.
///
/// A link issued by an older version has an older trigger — one that embeds the link's real
/// actions and runs them itself on every fetch, bypassing the allowance entirely — and a link
/// issued before a sharing-mode change has a trigger that accepts the wrong gesture. Neither
/// heals on its own, so every active link's trigger is checked at startup and re-armed in
/// place when it no longer embodies the current model (E7.S7.A1).
///
/// Never blocks startup, and retries while the home is unreachable.
/// </summary>
public class TriggerRearmService(
    IServiceProvider serviceProvider,
    ILogger<TriggerRearmService> logger)
    : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
    private const int MaxAttempts = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hand control back to startup immediately — this pass talks to the home.
        await Task.Yield();

        for (var attempt = 1; attempt <= MaxAttempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var linkService = scope.ServiceProvider.GetRequiredService<ILinkService>();
                var result = await linkService.RearmTriggersAsync(stoppingToken);

                if (result.Failed == 0)
                {
                    logger.LogInformation(
                        "Trigger check complete: {Rearmed} of {Checked} active link(s) re-armed " +
                        "to the current enforcement model",
                        result.Rearmed, result.Checked);
                    return;
                }

                logger.LogWarning(
                    "Trigger check incomplete: {Failed} link(s) could not be re-armed " +
                    "(attempt {Attempt}/{MaxAttempts})",
                    result.Failed, attempt, MaxAttempts);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not reach Home Assistant to check link triggers " +
                    "(attempt {Attempt}/{MaxAttempts})", attempt, MaxAttempts);
            }

            try
            {
                await Task.Delay(RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(
                "Gave up re-arming link triggers after {MaxAttempts} attempts — links may still " +
                "be enforced by an older trigger. Restart the add-on once Home Assistant is reachable.",
                MaxAttempts);
        }
    }
}
