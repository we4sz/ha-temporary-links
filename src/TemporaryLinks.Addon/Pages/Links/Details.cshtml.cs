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

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Link = await _linkService.GetLinkByIdAsync(id);

        LinkUrl = Link?.CloudhookUrl??"";

        return Page();
    }
}
