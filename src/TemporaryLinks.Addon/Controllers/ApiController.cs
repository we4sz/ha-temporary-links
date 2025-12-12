using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using TemporaryLinks.Addon.Services;

namespace TemporaryLinks.Addon.Controllers;

[ApiController]
[Route("api")]
public class ApiController : ControllerBase
{
    private readonly ILinkService _linkService;
    private readonly ILogger<ApiController> _logger;

    public ApiController(ILinkService linkService, ILogger<ApiController> logger)
    {
        _linkService = linkService;
        _logger = logger;
    }

    [HttpPost("links")]
    public async Task<IActionResult> CreateLink([FromBody] CreateLinkRequest request)
    {
        _logger.LogInformation("API: Creating link '{Name}' for script {ScriptId}",
            request.Name, request.ScriptEntityId);

        try
        {
            var baseUrl = GetBaseUrl();
            var link = await _linkService.CreateLinkAsync(
                name: request.Name,
                scriptEntityId: request.ScriptEntityId,
                validFrom: request.ValidFrom,
                validUntil: request.ValidUntil,
                recipientPhoneNumber: request.RecipientPhoneNumber,
                customMessage: request.CustomMessage,
                scriptData: request.ScriptData != null ? JsonSerializer.Serialize(request.ScriptData) : null,
                createdBy: "API/Automation",
                baseUrl: baseUrl,
                sendSmsImmediately: request.SendSms);

            var fullUrl = $"{baseUrl.TrimEnd('/')}/link/{link.Token}";

            return Ok(new CreateLinkResponse
            {
                Id = link.Id,
                Token = link.Token,
                Url = fullUrl,
                SmsSent = link.SmsSent,
                ValidFrom = link.ValidFrom,
                ValidUntil = link.ValidUntil
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create link via API");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("links/{token}")]
    public async Task<IActionResult> GetLinkStatus(string token)
    {
        var link = await _linkService.GetLinkByTokenAsync(token);
        if (link == null)
        {
            return NotFound(new { error = "Link not found" });
        }

        return Ok(new LinkStatusResponse
        {
            Id = link.Id,
            Token = link.Token,
            Name = link.Name,
            Status = link.Status.ToString(),
            ValidFrom = link.ValidFrom,
            ValidUntil = link.ValidUntil,
            UsedAt = link.UsedAt,
            SmsSent = link.SmsSent
        });
    }

    [HttpDelete("links/{token}")]
    public async Task<IActionResult> RevokeLink(string token)
    {
        var success = await _linkService.RevokeLinkAsync(token);
        if (!success)
        {
            return NotFound(new { error = "Link not found or cannot be revoked" });
        }

        return Ok(new { message = "Link revoked" });
    }

    [HttpGet("links")]
    public async Task<IActionResult> ListLinks([FromQuery] string? status = null)
    {
        var links = await _linkService.GetLinksAsync(status);

        return Ok(links.Select(l => new LinkStatusResponse
        {
            Id = l.Id,
            Token = l.Token,
            Name = l.Name,
            Status = l.Status.ToString(),
            ValidFrom = l.ValidFrom,
            ValidUntil = l.ValidUntil,
            UsedAt = l.UsedAt,
            SmsSent = l.SmsSent
        }));
    }

    private string GetBaseUrl()
    {
        var ingressPath = Request.Headers["X-Ingress-Path"].FirstOrDefault();
        if (!string.IsNullOrEmpty(ingressPath))
        {
            return ingressPath.TrimEnd('/');
        }
        return $"{Request.Scheme}://{Request.Host}";
    }
}

public class CreateLinkRequest
{
    public required string Name { get; set; }
    public required string ScriptEntityId { get; set; }
    public required DateTimeOffset ValidFrom { get; set; }
    public required DateTimeOffset ValidUntil { get; set; }
    public string? RecipientPhoneNumber { get; set; }
    public string? CustomMessage { get; set; }
    public object? ScriptData { get; set; }
    public bool SendSms { get; set; } = true;
}

public class CreateLinkResponse
{
    public int Id { get; set; }
    public required string Token { get; set; }
    public required string Url { get; set; }
    public bool SmsSent { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidUntil { get; set; }
}

public class LinkStatusResponse
{
    public int Id { get; set; }
    public required string Token { get; set; }
    public required string Name { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidUntil { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public bool SmsSent { get; set; }
}
