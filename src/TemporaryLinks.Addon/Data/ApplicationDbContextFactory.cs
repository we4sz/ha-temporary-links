using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TemporaryLinks.Addon.Data;

public class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();

        // Use SQLite with a dummy connection string for migrations
        optionsBuilder.UseSqlite("Data Source=temporarylinks.db");

        return new ApplicationDbContext(optionsBuilder.Options);
    }
}
