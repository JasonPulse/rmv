using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Rmv.Web.Data;

/// <summary>
/// Also the Data Protection key store. Without a shared key ring, ASP.NET Core
/// generates keys per process: sign-in cookies would break on every redeploy and
/// would not validate across replicas. Keeping the ring in Postgres means any
/// number of pods can decrypt each other's cookies.
/// </summary>
public class RmvDbContext(DbContextOptions<RmvDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Deployment> Deployments => Set<Deployment>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

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

        b.Entity<DataProtectionKey>().ToTable("data_protection_keys");
    }
}
