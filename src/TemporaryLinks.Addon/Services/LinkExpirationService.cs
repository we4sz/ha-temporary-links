namespace TemporaryLinks.Addon.Services;

public class LinkExpirationService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LinkExpirationService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    public LinkExpirationService(
        IServiceProvider serviceProvider,
        ILogger<LinkExpirationService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Link expiration service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var linkService = scope.ServiceProvider.GetRequiredService<ILinkService>();
                await linkService.ExpireOldLinksAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in link expiration service");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }
}
