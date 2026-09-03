using Microsoft.EntityFrameworkCore;
using WorkLedger.Server.Data;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Auth;

/// <summary>
/// Resolves the <i>pre-S6 legacy tenant</i>, if this deployment has one: <b>exactly one</b>
/// <see cref="User"/> row, and that row has no <c>PasswordHash</c>. See <see cref="User"/> for why such
/// a row exists at all.
/// <para>
/// <c>POST /auth/claim</c> and <c>GET /auth/capabilities</c>' <c>HasUnclaimedAccount</c> both resolve
/// through here, so the flag a client renders and the row the claim actually writes can never disagree
/// about what counts as claimable.
/// </para>
/// <para>
/// <b>Why "exactly one" and not "the oldest credential-less one".</b> Once a second user exists, the
/// deployment has moved past its single-tenant past and there is no longer an unambiguous "your data" to
/// hand to whoever presents the shared signup secret. Refusing is the only safe answer.
/// </para>
/// </summary>
internal static class UnclaimedUser
{
    public static async Task<User?> FindAsync(WorkLedgerDbContext db, CancellationToken cancellationToken)
    {
        // Take(2) rather than CountAsync + a second query: one round trip answers both "how many" and
        // "which one", and cannot see two different states if a registration lands in between.
        var users = await db.Users.OrderBy(u => u.Id).Take(2).ToListAsync(cancellationToken);
        return users.Count == 1 && users[0].PasswordHash is null ? users[0] : null;
    }
}
