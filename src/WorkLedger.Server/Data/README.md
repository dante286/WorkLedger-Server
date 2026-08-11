# Data layer

`WorkLedgerDbContext` (EF Core + Npgsql). Entities live in `Entities/`, migrations in `Migrations/`.

## Migrating on startup

`Program.cs` calls `Database.Migrate()` on boot when `Database:MigrateOnStartup` is `true` (the default —
see `appsettings.json`). This is the simple choice for a single-instance, self-hosted deployment; it would
need to become a separate deploy step if WorkLedger ever ran multiple API instances against the same
database, since concurrent `Migrate()` calls aren't safe to race.

Tests that boot the full app via `WebApplicationFactory<Program>` but don't need a real database (e.g.
`HealthEndpointTests`) override `Database:MigrateOnStartup` to `false` so they don't require Postgres.

## `dotnet ef` commands

Run these from `src/WorkLedger.Server/`. A `WorkLedgerDbContextFactory` (`IDesignTimeDbContextFactory`)
provides the design-time context, so these commands work without booting the app (and without a running
Postgres, when possible) — they fall back to the S0 dev-compose connection string if `ConnectionStrings:Postgres`
isn't otherwise configured.

```
dotnet ef migrations add <Name> -o Data/Migrations
dotnet ef database update
dotnet ef migrations remove
```
