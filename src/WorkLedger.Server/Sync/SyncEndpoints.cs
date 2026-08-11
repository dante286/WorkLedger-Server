using WorkLedger.Contracts.Sync;
using WorkLedger.Server.Auth;

namespace WorkLedger.Server.Sync;

public static class SyncEndpoints
{
    /// <summary>
    /// Both endpoints are account-scoped: AccountId comes from the caller's token, never from the
    /// request body, so a device physically cannot address another account's rows. Neither is marked
    /// AllowAnonymous, so Program.cs's authorization fallback policy protects them by default.
    /// </summary>
    public static void MapSyncEndpoints(this WebApplication app)
    {
        app.MapPost("/sync/push", async (
            SyncPushRequest request,
            SyncService sync,
            ICurrentContext currentContext,
            CancellationToken cancellationToken) =>
        {
            if (request?.Changes is null)
            {
                return Results.BadRequest(new { error = "Changes is required." });
            }

            var response = await sync.PushAsync(
                request, currentContext.AccountId, currentContext.DeviceId, cancellationToken);

            return Results.Ok(response);
        });

        app.MapGet("/sync/pull", async (
            string? since,
            SyncService sync,
            ICurrentContext currentContext,
            CancellationToken cancellationToken) =>
        {
            if (!SyncTime.TryParseSince(since, out var parsedSince))
            {
                return Results.BadRequest(new { error = $"'since' is not a valid ISO-8601 timestamp: '{since}'." });
            }

            var response = await sync.PullAsync(
                parsedSince, currentContext.AccountId, currentContext.DeviceId, cancellationToken);

            return Results.Ok(response);
        });
    }
}
