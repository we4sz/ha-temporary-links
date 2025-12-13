using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemporaryLinks.Addon.Models;
using TemporaryLinks.Addon.Services;

namespace TemporaryLinks.Addon.Pages.Links;

public class DeleteModel : PageModel
{
    private readonly ILinkService _linkService;

    public DeleteModel(ILinkService linkService)
    {
        _linkService = linkService;
    }

    public TemporaryLink? Link { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Link = await _linkService.GetLinkByIdAsync(id);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        Link = await _linkService.GetLinkByIdAsync(id);
        if (Link == null)
        {
            return NotFound();
        }

        await _linkService.RevokeLinkAsync(Link.Token);
        return RedirectToPage("Index");
    }
}
