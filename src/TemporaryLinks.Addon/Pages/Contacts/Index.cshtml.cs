using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Models;

namespace TemporaryLinks.Addon.Pages.Contacts;

public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _context;

    public IndexModel(ApplicationDbContext context)
    {
        _context = context;
    }

    public IList<Contact> Contacts { get; set; } = new List<Contact>();

    public async Task OnGetAsync()
    {
        Contacts = await _context.Contacts
            .OrderBy(c => c.Name)
            .ToListAsync();
    }
}
