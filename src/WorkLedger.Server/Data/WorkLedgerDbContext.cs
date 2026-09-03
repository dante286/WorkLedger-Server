using Microsoft.EntityFrameworkCore;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Data;

public sealed class WorkLedgerDbContext(DbContextOptions<WorkLedgerDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<TaskItem> TaskItems => Set<TaskItem>();
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
            b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
            b.Property(x => x.LockoutEndUtc).HasColumnType("timestamptz");
            b.Property(x => x.LastLoginUtc).HasColumnType("timestamptz");

            // Partial, not plain-unique: unclaimed pre-S6 rows carry a null NormalizedEmail and would
            // otherwise be limited to one per database by a full unique index. See User's XML doc.
            b.HasIndex(x => x.NormalizedEmail)
                .IsUnique()
                .HasFilter("\"NormalizedEmail\" IS NOT NULL");
        });

        modelBuilder.Entity<Device>(b =>
        {
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            b.HasIndex(x => new { x.UserId, x.DeviceId }).IsUnique();
            b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
            b.Property(x => x.LastSeenUtc).HasColumnType("timestamptz");
        });

        modelBuilder.Entity<Project>(b =>
        {
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            b.HasIndex(x => new { x.UserId, x.SyncId }).IsUnique();
            b.HasIndex(x => new { x.UserId, x.UpdatedAt });
            b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
            b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        });

        modelBuilder.Entity<TaskItem>(b =>
        {
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            b.HasIndex(x => new { x.UserId, x.SyncId }).IsUnique();
            b.HasIndex(x => new { x.UserId, x.UpdatedAt });
            b.HasIndex(x => new { x.UserId, x.ProjectSyncId });
            b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
            b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        });

        modelBuilder.Entity<TimeEntry>(b =>
        {
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            b.HasIndex(x => new { x.UserId, x.SyncId }).IsUnique();
            b.HasIndex(x => new { x.UserId, x.UpdatedAt });
            b.HasIndex(x => new { x.UserId, x.TaskSyncId });
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
