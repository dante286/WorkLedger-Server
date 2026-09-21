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
/// <c>POST /auth/logout-all</c>). S10 added a fourth to the same query: <b>account disabled</b>
/// (<c>POST /admin/users/{id}/disable</c>).
/// </para>
/// <para>
/// <b>S10 also made this the source of truth for who is an admin.</b> The same row the revocation
/// check already loads carries <c>IsAdmin</c>, so <see cref="OnTokenValidatedAsync"/> emits the
/// <see cref="AdminClaim"/> onto the validated principal rather than the claim riding in the token.
/// That is deliberate: tokens here last a year (see <see cref="JwtTokenService"/>), and an authorization
/// claim minted into one would outlive any decision to withdraw it. Resolving it per request means a
/// demotion behaves like every other revocation here — bounded by <c>Auth:StampCacheSeconds</c>, and
/// immediate in practice because the endpoints that demote evict the key. <b>Nothing this API mints
/// ever puts that claim in a token</b>, and any inbound claim of that type is stripped before this
/// decides, so the claim downstream policy sees is always one this method put there.
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

    /// <summary>
    /// The claim <see cref="OnTokenValidatedAsync"/> adds to the principal for a user whose
    /// <c>IsAdmin</c> column is set, and which the <see cref="AdminPolicy"/> requires. It is put there
    /// by this class from the database — never minted into a token, and stripped from the principal if
    /// a token somehow presents one.
    /// </summary>
    public const string AdminClaim = "admin";

    /// <summary>The authorization policy name every <c>/admin/*</c> route requires. Registered in <c>Program.cs</c>.</summary>
    public const string AdminPolicy = "admin";

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
            return;
        }

        if (snapshot.IsDisabled)
        {
            // Suspension revokes without touching SecurityStamp, the same way a removed device does:
            // the account is meant to come back intact when it is re-enabled, and rotating the stamp
            // would be a credential change it is not.
            context.Fail("This account is suspended.");
            return;
        }

        ApplyAdminClaim(context.Principal!, snapshot.IsAdmin);
    }

    /// <summary>
    /// Makes the principal's <see cref="AdminClaim"/> say exactly what the database says, by removing
    /// any inbound claim of that type first.
    /// <para>
    /// <b>The strip is not theatre, even though nothing mints that claim today.</b> This is the one
    /// claim that grants authority rather than describing identity, so it must be something this method
    /// decided and not something the request asserted. Costing one pass over the identities buys the
    /// guarantee unconditionally, instead of resting it on every future change to
    /// <see cref="JwtTokenService"/> remembering not to add the claim.
    /// </para>
    /// </summary>
    private static void ApplyAdminClaim(ClaimsPrincipal principal, bool isAdmin)
    {
        foreach (var identity in principal.Identities)
        {
            foreach (var inbound in identity.FindAll(AdminClaim).ToList())
            {
                // TryRemoveClaim rather than RemoveClaim: the latter throws for a claim the identity
                // does not own, and a principal assembled from several identities can hold one.
                identity.TryRemoveClaim(inbound);
            }
        }

        if (isAdmin)
        {
            principal.AddIdentity(new ClaimsIdentity([new Claim(AdminClaim, "true")]));
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
            .Join(
                db.Users,
                d => d.UserId,
                u => u.Id,
                (d, u) => new DeviceSnapshot(
                    d.UserId, u.SecurityStamp, u.IsAdmin, u.DisabledAtUtc != null))
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

    /// <summary>
    /// What one authenticated request needs to know about the caller, as four columns off the join this
    /// class was already doing. <c>IsAdmin</c> and <c>IsDisabled</c> ride along rather than earning a
    /// second query — and because they are cached with the rest of it, every endpoint that changes
    /// either one has to evict the key (see <see cref="EvictUserAsync"/>).
    /// </summary>
    private sealed record DeviceSnapshot(int UserId, string SecurityStamp, bool IsAdmin, bool IsDisabled);
}
