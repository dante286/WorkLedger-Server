using Microsoft.EntityFrameworkCore;
using WorkLedger.Contracts.Auth;
using WorkLedger.Server.Data;
using WorkLedger.Server.Data.Entities;

namespace WorkLedger.Server.Auth;

/// <summary>
/// Issue, list, revoke and look up invites (S10) — the replacement for handing a new colleague the
/// operator's own <c>Bootstrap:Secret</c>. Issuing and revoking are admin actions and are audited as
/// such; redemption happens inside <see cref="RegistrationService"/>, in the same transaction as the
/// account it creates.
/// <para>
/// <b>The raw token leaves the server exactly once</b>, in the result of <see cref="IssueAsync"/>. Only
/// its SHA-256 is stored (see <see cref="SingleUseToken"/>), so there is nothing and nowhere that can
/// produce it again — an operator who loses one revokes it and issues another.
/// </para>
/// </summary>
public sealed class InviteService(WorkLedgerDbContext db, AuthOptions auth, ILoggerFactory loggerFactory)
{
    /// <summary>
    /// Mints an invite, optionally bound to <paramref name="email"/>, living <paramref name="expiresInDays"/>
    /// days (<c>Auth:InviteExpiryDays</c> when null).
    /// </summary>
    public async Task<InviteIssue> IssueAsync(
        int actorUserId,
        string? email,
        int? expiresInDays,
        string? note,
        CancellationToken cancellationToken)
    {
        email = email?.Trim();

        // Validated with the same shallow rule registration applies, so an invite cannot be bound to an
        // address registration would then refuse — which would produce an invite that is impossible to
        // redeem, and no error explaining why.
        if (!string.IsNullOrEmpty(email) && !Credentials.LooksLikeEmail(email))
        {
            return InviteIssue.InvalidEmail.Instance;
        }

        var days = expiresInDays ?? auth.InviteExpiryDays;
        if (days <= 0)
        {
            return InviteIssue.InvalidExpiry.Instance;
        }

        var token = SingleUseToken.Generate();
        var now = DateTime.UtcNow;
        var invite = new Invite
        {
            TokenHash = SingleUseToken.Hash(token),
            NormalizedEmail = string.IsNullOrEmpty(email) ? null : email.ToUpperInvariant(),
            CreatedByUserId = actorUserId,
            CreatedAt = now,
            ExpiresAtUtc = now.AddDays(days),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        };
        db.Invites.Add(invite);
        await db.SaveChangesAsync(cancellationToken);

        // Ids only — and not the bound address either. This is the one admin action whose entire
        // product is a secret, so its log line has to be especially dull.
        AdminOperations.Audit(loggerFactory, actorUserId, "create-invite", actorUserId);

        return new InviteIssue.Issued(token, Summarize(invite));
    }

    /// <summary>Every invite, metadata only — the token is not recoverable. Newest first: an operator
    /// opening the list is almost always looking for the one they just made.</summary>
    public Task<List<InviteSummary>> ListAsync(CancellationToken cancellationToken) =>
        db.Invites
            .AsNoTracking()
            .OrderByDescending(i => i.Id)
            .Select(i => new InviteSummary(
                i.Id,
                i.NormalizedEmail,
                i.CreatedByUserId,
                i.CreatedAt,
                i.ExpiresAtUtc,
                i.RedeemedAtUtc,
                i.RedeemedByUserId,
                i.Note))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Withdraws an unredeemed invite.
    /// <para>
    /// <b>A redeemed invite is refused, not deleted.</b> Deleting it would erase the record of how a live
    /// account came to exist, which is most of why redeemed rows are kept at all. A spent invite is already
    /// unusable, so there is nothing to revoke — the caller wanting the account gone wants
    /// <see cref="AdminOperations.DeleteUserAsync"/> instead.
    /// </para>
    /// <para>
    /// <b>Revoking an unredeemed invite IS a hard delete</b>, unlike redeeming, which stamps the row. There
    /// is nothing to preserve: it created nothing, and a tombstone would only clutter the list an operator
    /// reads.
    /// </para>
    /// </summary>
    public async Task<InviteRevocation> RevokeAsync(
        int actorUserId, int inviteId, CancellationToken cancellationToken)
    {
        var invite = await db.Invites.SingleOrDefaultAsync(i => i.Id == inviteId, cancellationToken);
        if (invite is null)
        {
            return InviteRevocation.NotFound;
        }

        if (invite.RedeemedAtUtc is not null)
        {
            return InviteRevocation.AlreadyRedeemed;
        }

        db.Invites.Remove(invite);
        await db.SaveChangesAsync(cancellationToken);

        AdminOperations.Audit(loggerFactory, actorUserId, "revoke-invite", actorUserId);
        return InviteRevocation.Revoked;
    }

    /// <summary>
    /// The invite matching <paramref name="token"/>, locked for the rest of the caller's transaction, or
    /// null if there is no such invite.
    /// <para>
    /// <b><c>FOR UPDATE</c> is what makes redemption single-use under concurrency.</b> Two registrations
    /// presenting the same token would otherwise both read it unredeemed and both succeed, creating two
    /// accounts from one invite. With the lock the second blocks until the first commits, and Postgres
    /// re-evaluates the row once it is granted, so it sees the stamp and is refused.
    /// </para>
    /// <para>
    /// Matched on the hash, never on the token: the column holds only <c>SingleUseToken.Hash</c> output,
    /// so this is an indexed equality on a digest and the raw value never reaches a query. Not composed
    /// with further LINQ, for the reason <see cref="AdminOperations"/> gives — EF would wrap it in a
    /// subquery and <c>FOR UPDATE</c> has to stay on the outer statement.
    /// </para>
    /// </summary>
    public async Task<Invite?> FindForUpdateAsync(string token, CancellationToken cancellationToken)
    {
        var hash = SingleUseToken.Hash(token);
        var invites = await db.Invites
            .FromSql($"""SELECT * FROM "Invites" WHERE "TokenHash" = {hash} FOR UPDATE""")
            .ToListAsync(cancellationToken);
        return invites.SingleOrDefault();
    }

    /// <summary>The wire shape of an invite, minus the token. Shared so issue and list cannot drift.</summary>
    internal static InviteSummary Summarize(Invite invite) =>
        new(invite.Id,
            invite.NormalizedEmail,
            invite.CreatedByUserId,
            invite.CreatedAt,
            invite.ExpiresAtUtc,
            invite.RedeemedAtUtc,
            invite.RedeemedByUserId,
            invite.Note);
}

/// <summary>What <see cref="InviteService.IssueAsync"/> did.</summary>
public abstract record InviteIssue
{
    /// <summary>Minted. <see cref="Token"/> is the only copy there will ever be.</summary>
    public sealed record Issued(string Token, InviteSummary Invite) : InviteIssue;

    /// <summary>The bound address fails registration's email rule, so the invite could never be redeemed.</summary>
    public sealed record InvalidEmail : InviteIssue
    {
        public static readonly InvalidEmail Instance = new();
    }

    /// <summary>The requested lifetime is zero or negative.</summary>
    public sealed record InvalidExpiry : InviteIssue
    {
        public static readonly InvalidExpiry Instance = new();
    }
}

public enum InviteRevocation
{
    Revoked,
    NotFound,
    AlreadyRedeemed,
}
