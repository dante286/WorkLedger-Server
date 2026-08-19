# Data layer

`WorkLedgerDbContext` (EF Core + Npgsql). Entities live in `Entities/`, migrations in `Migrations/`.

## Which database

`PostgresConnection.Resolve` is the single resolver, used by both the running app and the `dotnet ef`
tooling below: `ConnectionStrings:Postgres` if set, else the four `POSTGRES_HOST`/`POSTGRES_DB`/
`POSTGRES_USER`/`POSTGRES_PASSWORD` variables that `docker-compose.yml` passes. The dev connection string
(`localhost:15432`) lives in `appsettings.Development.json`, deliberately not in `appsettings.json`, so a
Production instance with no database configured fails fast naming the key instead of quietly dialling
localhost.

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
Postgres, when possible). It reads `appsettings.json`, `appsettings.Development.json` and the
environment, so from the project directory the dev connection string is found with no extra setup; set
`ConnectionStrings__Postgres` or the `POSTGRES_*` variables to point the tooling at another database.

```
dotnet ef migrations add <Name> -o Data/Migrations
dotnet ef database update
dotnet ef migrations remove
```
