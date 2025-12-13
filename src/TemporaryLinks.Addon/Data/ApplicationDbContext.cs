using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TemporaryLinks.Addon.Models;

namespace TemporaryLinks.Addon.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<TemporaryLink> TemporaryLinks => Set<TemporaryLink>();
    public DbSet<LinkUsageAudit> LinkUsageAudits => Set<LinkUsageAudit>();
    public DbSet<LinkSmsAudit> LinkSmsAudits => Set<LinkSmsAudit>();
    public DbSet<Contact> Contacts => Set<Contact>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite doesn't support DateTimeOffset natively, store as ticks for proper comparison
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<DateTimeOffset?>()
            .HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TemporaryLink>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Token).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ValidUntil);

            entity.Property(e => e.Token).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.Property(e => e.RecipientPhoneNumber).HasMaxLength(20);
            entity.Property(e => e.CreatedBy).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Status).HasConversion<int>();
        });

        modelBuilder.Entity<LinkUsageAudit>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TemporaryLinkId);
            entity.HasIndex(e => e.Timestamp);

            entity.HasOne(e => e.TemporaryLink)
                .WithMany(e => e.AuditEntries)
                .HasForeignKey(e => e.TemporaryLinkId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        
        modelBuilder.Entity<LinkSmsAudit>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TemporaryLinkId);
            entity.HasIndex(e => e.Timestamp);

            entity.HasOne(e => e.TemporaryLink)
                .WithMany(e => e.SmsEntries)
                .HasForeignKey(e => e.TemporaryLinkId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Contact>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.PhoneNumber);

            entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
            entity.Property(e => e.PhoneNumber).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(256);
            entity.Property(e => e.Info).HasMaxLength(1000);
        });
    }
}
