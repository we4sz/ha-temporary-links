using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Models;

namespace TemporaryLinks.Addon.Pages.ActionTemplates;

public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _context;

    public IndexModel(ApplicationDbContext context)
    {
        _context = context;
    }

    public IList<ActionTemplate> Templates { get; set; } = new List<ActionTemplate>();

    public async Task OnGetAsync()
    {
        Templates = await _context.ActionTemplates
            .OrderBy(t => t.Name)
            .ToListAsync();
    }
}
