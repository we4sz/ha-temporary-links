using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Models;

namespace TemporaryLinks.Addon.Pages.ActionTemplates;

public class DeleteModel : PageModel
{
    private readonly ApplicationDbContext _context;

    public DeleteModel(ApplicationDbContext context)
    {
        _context = context;
    }

    public ActionTemplate? Template { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Template = await _context.ActionTemplates.FindAsync(id);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        Template = await _context.ActionTemplates.FindAsync(id);

        if (Template == null)
        {
            return NotFound();
        }

        _context.ActionTemplates.Remove(Template);
        await _context.SaveChangesAsync();

        return RedirectToPage("Index");
    }
}
