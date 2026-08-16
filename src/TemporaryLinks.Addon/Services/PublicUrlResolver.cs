using TemporaryLinks.Addon.Configuration;

namespace TemporaryLinks.Addon.Services;

/// <summary>
/// Resolves the public base URL the add-on serves its own confirm page from: a manually
/// configured <c>public_url</c> always wins; otherwise the URL is discovered from the home's
/// cloud remote access, so a normal Nabu Casa install needs zero configuration. With neither,
/// the value stays null — and unless a shared page is configured, creating a link is refused.
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
            "No public URL configured or discoverable — unless share_page_url is set, " +
            "creating a link will be refused (there would be no confirm page to share).");
        return null;
    }
}
