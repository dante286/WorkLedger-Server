using WorkLedger.Contracts.Sync;
using WorkLedger.Server.Auth;

namespace WorkLedger.Server.Sync;

public static class SyncEndpoints
{
    /// <summary>
    /// Both endpoints are user-scoped: UserId comes from the caller's token, never from the
    /// request body, so a device physically cannot address another user's rows. Neither is marked
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
                request, currentContext.UserId, currentContext.DeviceId, cancellationToken);

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
                parsedSince, currentContext.UserId, currentContext.DeviceId, cancellationToken);

            return Results.Ok(response);
        });
    }
}
