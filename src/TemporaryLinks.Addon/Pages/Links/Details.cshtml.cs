using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;

namespace TemporaryLinks.Addon.Pages.Links;

public class DetailsModel : PageModel
{
    private readonly ILinkService _linkService;

    public DetailsModel(ILinkService linkService)
    {
        _linkService = linkService;
    }

    public TemporaryLink? Link { get; set; }
    public string LinkUrl { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        Link = await _linkService.GetLinkByIdAsync(id);

        if (Link != null)
        {
            var baseUrl = GetBaseUrl();
            LinkUrl = $"{baseUrl.TrimEnd('/')}/link/{Link.Token}";
        }

        return Page();
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
