using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;

namespace TemporaryLinks.Addon.Pages.Links;

public class IndexModel : PageModel
{
    private readonly ILinkService _linkService;

    public IndexModel(ILinkService linkService)
    {
        _linkService = linkService;
    }

    public IList<TemporaryLink> Links { get; set; } = [];

    [BindProperty(SupportsGet = true)]
    public string? StatusFilter { get; set; }

    public async Task OnGetAsync()
    {
        Links = await _linkService.GetLinksAsync(StatusFilter);
    }
}
