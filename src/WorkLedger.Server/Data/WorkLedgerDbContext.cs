using Microsoft.EntityFrameworkCore;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Data;

public sealed class WorkLedgerDbContext(DbContextOptions<WorkLedgerDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<PasswordReset> PasswordResets => Set<PasswordReset>();
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
            b.Property(x => x.DisabledAtUtc).HasColumnType("timestamptz");

            // Partial, covering exactly the set AdminEndpoints' last-admin guard counts and locks. On a
            // table this size an index is not about speed — it is that the guard's `FOR UPDATE` should
            // lock the two or three admin rows rather than take a predicate lock behind a sequential
            // scan of every user.
            b.HasIndex(x => x.IsAdmin)
                .HasFilter("\"IsAdmin\" AND \"DisabledAtUtc\" IS NULL");

            // Partial, not plain-unique: unclaimed pre-S6 rows carry a null NormalizedEmail and would
            // otherwise be limited to one per database by a full unique index. See User's XML doc.
            b.HasIndex(x => x.NormalizedEmail)
                .IsUnique()
                .HasFilter("\"NormalizedEmail\" IS NOT NULL");
        });

        modelBuilder.Entity<Invite>(b =>
        {
            // SET NULL, not the cascade every other table here uses. An invite is a record of how an
            // account came to exist, so it has to survive the deletion of the admin who issued it and of
            // the user who redeemed it — see Invite's XML doc. This is also what keeps the two
            // account-deletion pipelines unchanged: Postgres nulls these columns itself.
            b.HasOne<User>().WithMany()
                .HasForeignKey(x => x.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            b.HasOne<User>().WithMany()
                .HasForeignKey(x => x.RedeemedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => x.CreatedByUserId);
            b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
            b.Property(x => x.ExpiresAtUtc).HasColumnType("timestamptz");
            b.Property(x => x.RedeemedAtUtc).HasColumnType("timestamptz");
        });

        modelBuilder.Entity<PasswordReset>(b =>
        {
            // Two different behaviours on purpose — see PasswordReset's XML doc. The reset dies with the
            // account it resets (nothing left to unlock), but outlives the admin who issued it (the
            // record of an intervention on an account that still exists is worth keeping). Cascading on
            // UserId is also what keeps the account-deletion pipelines unaware of this table.
            b.HasOne<User>().WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany()
                .HasForeignKey(x => x.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => x.UserId);
            b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
            b.Property(x => x.ExpiresAtUtc).HasColumnType("timestamptz");
            b.Property(x => x.RedeemedAtUtc).HasColumnType("timestamptz");
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
