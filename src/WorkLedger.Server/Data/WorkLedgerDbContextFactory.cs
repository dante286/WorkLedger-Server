using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace WorkLedger.Server.Data;

/// <summary>
/// Used only by `dotnet ef` tooling. Without this, EF's design-time tooling falls back to
/// invoking Program.cs up to (but not including) app.Run() — which would also run our
/// Migrate()-on-boot call against a real Postgres just to generate a migration.
/// <para>
/// The connection string comes from <see cref="PostgresConnection"/>, the same resolver the running app
/// uses, so there is no second definition of "which database" to drift: appsettings.Development.json
/// supplies the dev one (these commands run from the project directory), and POSTGRES_* or
/// ConnectionStrings__Postgres in the environment points the tooling at any other.
/// </para>
/// </summary>
public sealed class WorkLedgerDbContextFactory : IDesignTimeDbContextFactory<WorkLedgerDbContext>
{
    public WorkLedgerDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var optionsBuilder = new DbContextOptionsBuilder<WorkLedgerDbContext>();
        optionsBuilder.UseNpgsql(PostgresConnection.Resolve(configuration).ConnectionString);

        return new WorkLedgerDbContext(optionsBuilder.Options);
    }
}
