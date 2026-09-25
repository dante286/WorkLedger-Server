using Microsoft.EntityFrameworkCore;
using WorkLedger.Server.Data;

namespace WorkLedger.Server.Auth;

/// <summary>
/// How far this server is through being set up, which decides the one thing <c>/portal/setup</c> offers
/// (S11). Every state but <see cref="ServerSetupState.Ready"/> is one where nobody can administer the
/// server yet, so each one's way forward is gated on the operator's secret.
/// <para>
/// The states are checked in order, and each is a strict answer to "what is the one missing step":
/// no users at all; the single credential-less pre-S6 row (<see cref="UnclaimedUser"/>, the same test
/// <c>/auth/claim</c> and <c>/auth/capabilities</c> use); users but no admin who can sign in (the same
/// predicate <see cref="AdminOperations.BootstrapAsync"/> refuses on); or set up.
/// </para>
/// </summary>
public static class ServerSetup
{
    public static async Task<ServerSetupState> ResolveAsync(WorkLedgerDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(cancellationToken))
        {
            return ServerSetupState.Empty;
        }

        if (await UnclaimedUser.FindAsync(db, cancellationToken) is not null)
        {
            return ServerSetupState.Unclaimed;
        }

        return await db.Users.AnyAsync(u => u.IsAdmin && u.DisabledAtUtc == null, cancellationToken)
            ? ServerSetupState.Ready
            : ServerSetupState.NoAdmin;
    }
}

public enum ServerSetupState
{
    /// <summary>No users: the next step is the first account, which is the administrator.</summary>
    Empty,

    /// <summary>Only the pre-S6 legacy row, without credentials: the next step is claiming it.</summary>
    Unclaimed,

    /// <summary>Users, but no admin who can sign in: the next step is one of them promoting themselves.</summary>
    NoAdmin,

    /// <summary>An admin exists. Nothing is left to set up, and the setup page does not exist.</summary>
    Ready,
}
