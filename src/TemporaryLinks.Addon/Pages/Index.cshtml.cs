using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TemporaryLinks.Addon.Data;
using TemporaryLinks.Addon.Models;

namespace TemporaryLinks.Addon.Pages;

public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _context;

    public IndexModel(ApplicationDbContext context)
    {
        _context = context;
    }

    public int TotalLinks { get; set; }
    public int ActiveLinks { get; set; }
    public int UsedLinks { get; set; }
    public int ExpiredLinks { get; set; }
    public IList<TemporaryLink> ActiveLinksList { get; set; } = [];
    public IList<LinkUsageAudit> RecentActivity { get; set; } = [];

    public async Task OnGetAsync()
    {
        TotalLinks = await _context.TemporaryLinks.CountAsync();
        ActiveLinks = await _context.TemporaryLinks.CountAsync(l => l.Status == LinkStatus.Active);
        UsedLinks = await _context.TemporaryLinks.CountAsync(l => l.Status == LinkStatus.Used);
        ExpiredLinks = await _context.TemporaryLinks.CountAsync(l => l.Status == LinkStatus.Expired);

        ActiveLinksList = await _context.TemporaryLinks
            .Where(l => l.Status == LinkStatus.Active)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();

        RecentActivity = await _context.LinkUsageAudits
            .Include(a => a.TemporaryLink)
            .OrderByDescending(a => a.Timestamp)
            .Take(10)
            .ToListAsync();
    }
}
