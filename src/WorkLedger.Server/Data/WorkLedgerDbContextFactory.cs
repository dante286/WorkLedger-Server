using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace WorkLedger.Server.Data;

/// <summary>
/// Used only by `dotnet ef` tooling. Without this, EF's design-time tooling falls back to
/// invoking Program.cs up to (but not including) app.Run() — which would also run our
/// Migrate()-on-boot call against a real Postgres just to generate a migration.
/// </summary>
public sealed class WorkLedgerDbContextFactory : IDesignTimeDbContextFactory<WorkLedgerDbContext>
{
    public WorkLedgerDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("Postgres")
            ?? "Host=localhost;Port=55432;Database=workledger;Username=workledger;Password=workledger";

        var optionsBuilder = new DbContextOptionsBuilder<WorkLedgerDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new WorkLedgerDbContext(optionsBuilder.Options);
    }
}
