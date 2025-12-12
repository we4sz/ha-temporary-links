using Microsoft.AspNetCore.Mvc;
using TemporaryLinks.Addon.Services;

namespace TemporaryLinks.Addon.Controllers;

[Controller]
public class LinkController : Controller
{
    private readonly ILinkService _linkService;
    private readonly ILogger<LinkController> _logger;

    public LinkController(
        ILinkService linkService,
        ILogger<LinkController> logger)
    {
        _linkService = linkService;
        _logger = logger;
    }

    [HttpGet("/link/{token}")]
    public async Task<IActionResult> Execute(string token)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();

        _logger.LogInformation("Link execution attempt for token {Token} from IP {IP}",
            token, ipAddress);

        var result = await _linkService.ExecuteLinkAsync(token, ipAddress, userAgent);

        return result.Status switch
        {
            LinkExecutionStatus.Success => View("Success", result),
            LinkExecutionStatus.NotFound => View("NotFound"),
            LinkExecutionStatus.AlreadyUsed => View("AlreadyUsed", result),
            LinkExecutionStatus.NotYetValid => View("NotYetValid", result),
            LinkExecutionStatus.Expired => View("Expired", result),
            LinkExecutionStatus.Revoked => View("Revoked"),
            LinkExecutionStatus.Error => View("Error", result),
            _ => View("Error", result)
        };
    }
}
