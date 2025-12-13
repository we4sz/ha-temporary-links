using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Models;

namespace TemporaryLinks.Addon.Pages.Contacts;

public class DeleteModel : PageModel
{
    private readonly ApplicationDbContext _context;

    public DeleteModel(ApplicationDbContext context)
    {
        _context = context;
    }

    public Contact? Contact { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Contact = await _context.Contacts.FindAsync(id);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        Contact = await _context.Contacts.FindAsync(id);

        if (Contact == null)
        {
            return NotFound();
        }

        _context.Contacts.Remove(Contact);
        await _context.SaveChangesAsync();

        return RedirectToPage("Index");
    }
}
