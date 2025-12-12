using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Models;

namespace TemporaryLinks.Addon.Pages.Audit;

public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _context;

    public IndexModel(ApplicationDbContext context)
    {
        _context = context;
    }

    public IList<LinkUsageAudit> AuditEntries { get; set; } = [];

    public async Task OnGetAsync()
    {
        AuditEntries = await _context.LinkUsageAudits
            .Include(a => a.TemporaryLink)
            .OrderByDescending(a => a.Timestamp)
            .Take(100)
            .ToListAsync();
    }
}
