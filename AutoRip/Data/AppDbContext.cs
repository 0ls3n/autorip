using Microsoft.EntityFrameworkCore;
using AutoRip.Models;

namespace AutoRip.Data;

public class AppDbContext : DbContext
{
    public DbSet<SettingEntity> Settings { get; set; }
    public DbSet<RipJob> RipJobs { get; set; }
    public DbSet<RipLogEntry> RipLogs { get; set; }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RipJob>(entity =>
        {
            entity.HasIndex(j => j.CreatedAt);
            entity.HasIndex(j => j.Status);
        });

        modelBuilder.Entity<RipLogEntry>(entity =>
        {
            entity.HasIndex(l => l.RipJobId);
            entity.HasIndex(l => l.Timestamp);
            entity.HasOne(l => l.RipJob)
                  .WithMany()
                  .HasForeignKey(l => l.RipJobId);
        });
    }
}
