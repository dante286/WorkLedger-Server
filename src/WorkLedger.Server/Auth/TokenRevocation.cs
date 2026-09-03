using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WorkLedger.Server.Data;

namespace WorkLedger.Server.Auth;

/// <summary>
/// The revocation mechanism: what makes a long-lived token killable, and therefore the substitute for
/// short token expiry (see <see cref="JwtTokenService"/> for why expiry is long).
/// <para>
/// Every token carries the user's <c>SecurityStamp</c> as a <c>stamp</c> claim.
/// <see cref="OnTokenValidatedAsync"/> loads the token's device row <b>joined to its user</b> in one
/// query and fails the token — a plain <c>401</c> — if the device row is gone, its <c>UserId</c>
/// disagrees with the <c>user_id</c> claim, or the stored stamp no longer matches the claim. That single
/// query covers three separate revocations, and S8 gave each of them an endpoint:
/// <b>account deleted</b> (<c>DELETE /auth/account</c>), <b>device removed</b>
/// (<c>DELETE /auth/devices/{deviceId}</c>), and <b>stamp bumped</b> (<c>POST /auth/password</c> and
/// <c>POST /auth/logout-all</c>).
/// </para>
/// <para>
/// <b>Ordering matters and is guaranteed.</b> <c>OnTokenValidated</c> fires only after the signature,
/// issuer, audience and lifetime have all been validated, so a forged or expired token is rejected
/// before it can reach the database. Nothing here is a substitute for those checks; it runs on top of
/// them.
/// </para>
/// <para>
/// <b><c>Auth:StampCacheSeconds</c> is the revocation lag, and that is the whole tradeoff.</b> Without a
/// cache this would put a database read on <i>every</i> authenticated request — every sync push, every
/// pull, every report. With it, a token that has just been revoked keeps working for up to that many
/// seconds (default <c>30</c>; <c>0</c> disables the cache and makes revocation immediate at the cost of
/// that read). The lag is bounded and, in the common case, not observed at all: the code paths that
/// revoke evict the affected keys directly through <see cref="EvictUserAsync"/>, so the TTL only matters
/// when the change came from somewhere this process cannot see — a <c>psql</c> <c>UPDATE</c> against the
/// volume, or another replica.
/// </para>
/// </summary>
internal static class TokenRevocation
{
    /// <summary>The claim carrying the user's <c>SecurityStamp</c> as of the moment the token was minted.</summary>
    public const string StampClaim = "stamp";

    public static async Task OnTokenValidatedAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (principal is null ||
            !TryReadDeviceRowId(principal, out var deviceRowId) ||
            !int.TryParse(principal.FindFirstValue("user_id"), out var claimedUserId))
        {
            context.Fail("Token is missing the device or user claims this API mints.");
            return;
        }

        var claimedStamp = principal.FindFirstValue(StampClaim);
        if (string.IsNullOrEmpty(claimedStamp))
        {
            // A token from before S7 — validly signed, but with no stamp to check. Refusing it is the
            // point: an unstampable token would be permanently unrevokable, and every client re-logs in
            // on this release anyway.
            context.Fail("Token carries no security stamp. Sign in again.");
            return;
        }

        var services = context.HttpContext.RequestServices;
        var snapshot = await ResolveAsync(
            services.GetRequiredService<WorkLedgerDbContext>(),
            services.GetRequiredService<IMemoryCache>(),
            services.GetRequiredService<AuthOptions>(),
            deviceRowId,
            context.HttpContext.RequestAborted);

        if (snapshot is null)
        {
            context.Fail("The device this token was issued to no longer exists.");
            return;
        }

        if (snapshot.UserId != claimedUserId)
        {
            // Identity columns are never reused, so this is not a device that changed hands — it is a
            // token whose two halves disagree, which nothing legitimate produces.
            context.Fail("Token's user does not own the device it was issued to.");
            return;
        }

        if (!string.Equals(snapshot.SecurityStamp, claimedStamp, StringComparison.Ordinal))
        {
            context.Fail("Token was revoked by a credential change.");
        }
    }

    /// <summary>
    /// Drops every cached snapshot belonging to <paramref name="userId"/>, so a stamp bump takes effect
    /// on the next request rather than after <c>Auth:StampCacheSeconds</c>. Call it <b>after</b> the new
    /// stamp is committed — evicting first would just let the next request re-cache the old value.
    /// </summary>
    public static async Task EvictUserAsync(
        WorkLedgerDbContext db,
        IMemoryCache cache,
        int userId,
        CancellationToken cancellationToken)
    {
        var deviceRowIds = await db.Devices
            .Where(d => d.UserId == userId)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        foreach (var deviceRowId in deviceRowIds)
        {
            cache.Remove(CacheKey(deviceRowId));
        }
    }

    public static string CacheKey(int deviceRowId) => $"device:{deviceRowId}";

    /// <summary>
    /// One projection of <c>(UserId, SecurityStamp)</c> per device row, cached for
    /// <c>Auth:StampCacheSeconds</c>. A <b>miss is cached too</b>: a revoked device's token would
    /// otherwise re-query on every request it keeps making, and a negative entry cannot shadow a real
    /// device because Postgres identity ids are never reused.
    /// </summary>
    private static async Task<DeviceSnapshot?> ResolveAsync(
        WorkLedgerDbContext db,
        IMemoryCache cache,
        AuthOptions auth,
        int deviceRowId,
        CancellationToken cancellationToken)
    {
        var key = CacheKey(deviceRowId);
        if (auth.StampCacheSeconds > 0 && cache.TryGetValue(key, out DeviceSnapshot? cached))
        {
            return cached;
        }

        var snapshot = await db.Devices
            .Where(d => d.Id == deviceRowId)
            .Join(db.Users, d => d.UserId, u => u.Id, (d, u) => new DeviceSnapshot(d.UserId, u.SecurityStamp))
            .SingleOrDefaultAsync(cancellationToken);

        if (auth.StampCacheSeconds > 0)
        {
            cache.Set(key, snapshot, TimeSpan.FromSeconds(auth.StampCacheSeconds));
        }

        return snapshot;
    }

    /// <summary>
    /// The device row id lives in <c>sub</c>, but the JWT bearer handler maps inbound claims by default,
    /// so by the time a <see cref="ClaimsPrincipal"/> exists that claim has usually been renamed to
    /// <see cref="ClaimTypes.NameIdentifier"/>. Both names are accepted rather than turning the mapping
    /// off, which would quietly change every other claim's shape too.
    /// </summary>
    public static bool TryReadDeviceRowId(ClaimsPrincipal principal, out int deviceRowId)
    {
        var value = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out deviceRowId);
    }

    private sealed record DeviceSnapshot(int UserId, string SecurityStamp);
}
