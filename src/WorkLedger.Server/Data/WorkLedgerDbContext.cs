using Microsoft.EntityFrameworkCore;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Data;

public sealed class WorkLedgerDbContext(DbContextOptions<WorkLedgerDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<TaskItem> TaskItems => Set<TaskItem>();
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(b =>
        {
            b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        });

        modelBuilder.Entity<Device>(b =>
        {
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
            b.HasIndex(x => new { x.AccountId, x.DeviceId }).IsUnique();
            b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
            b.Property(x => x.LastSeenUtc).HasColumnType("timestamptz");
        });

        modelBuilder.Entity<Project>(b =>
        {
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
            b.HasIndex(x => new { x.AccountId, x.SyncId }).IsUnique();
            b.HasIndex(x => new { x.AccountId, x.UpdatedAt });
            b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
            b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        });

        modelBuilder.Entity<TaskItem>(b =>
        {
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
            b.HasIndex(x => new { x.AccountId, x.SyncId }).IsUnique();
            b.HasIndex(x => new { x.AccountId, x.UpdatedAt });
            b.HasIndex(x => new { x.AccountId, x.ProjectSyncId });
            b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
            b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        });

        modelBuilder.Entity<TimeEntry>(b =>
        {
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId);
            b.HasIndex(x => new { x.AccountId, x.SyncId }).IsUnique();
            b.HasIndex(x => new { x.AccountId, x.UpdatedAt });
            b.HasIndex(x => new { x.AccountId, x.TaskSyncId });
            b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
            b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
            b.Property(x => x.StartTime).HasColumnType("timestamptz");
            b.Property(x => x.EndTime).HasColumnType("timestamptz");
        });
    }

    public override int SaveChanges()
    {
        StampUpdatedAt();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampUpdatedAt();
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The pull cursor (S3) sorts/filters on UpdatedAt, so it must never be poisoned by client
    /// clock skew — every added/modified synced entity gets the server's own clock here,
    /// unconditionally, regardless of whatever value the caller set.
    /// </summary>
    private void StampUpdatedAt()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<IVersionedSyncEntity>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }
}
