using TemporaryLinks.Addon.Configuration;

namespace TemporaryLinks.Addon.Services;

/// <summary>
/// Resolves the public base URL for shared links at startup: a manually configured
/// <c>public_url</c> always wins; otherwise the URL is discovered from the home's cloud
/// remote access, so a normal Nabu Casa install needs zero configuration. With neither,
/// the value stays null and links fall back to the direct (GET) form.
/// </summary>
public static class PublicUrlResolver
{
    public static async Task<string?> ResolveAsync(
        AddonConfiguration config,
        IHomeAssistantService haService,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(config.PublicUrl))
        {
            logger.LogInformation("Using configured public_url: {Url}", config.PublicUrl);
            return config.PublicUrl;
        }

        var discovered = await haService.GetRemoteUiUrlAsync(cancellationToken);
        if (discovered != null)
        {
            config.PublicUrl = discovered;
            logger.LogInformation(
                "public_url not configured — using the home's remote UI URL: {Url}", discovered);
            return discovered;
        }

        logger.LogInformation(
            "No public URL configured or discoverable — shared links use the direct cloudhook form.");
        return null;
    }
}
