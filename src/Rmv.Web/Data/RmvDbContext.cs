using Microsoft.EntityFrameworkCore;

namespace Rmv.Web.Data;

public class RmvDbContext(DbContextOptions<RmvDbContext> options) : DbContext(options)
{
    public DbSet<Deployment> Deployments => Set<Deployment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Deployment>(e =>
        {
            e.ToTable("deployments");
            e.Property(d => d.Version).HasMaxLength(64).IsRequired();
            e.Property(d => d.Host).HasMaxLength(128).IsRequired();
            // Newest-first is the only way this table is ever read.
            e.HasIndex(d => d.StartedAt).IsDescending();
        });
    }
}
