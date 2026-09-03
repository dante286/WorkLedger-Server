namespace WorkLedger.Server.Data.Entities;

/// <summary>
/// The top of the tenancy tree (S6): one user owns many <see cref="Device"/> rows, and every device
/// syncs the same projects/tasks/time entries back under this row's <c>Id</c>. Replaces the S2
/// <c>Account</c> entity — a <c>User</c>→<c>Account</c> 1:N split was considered and rejected, because
/// v1 has no multi-user tenancy to model and the extra join would be dead weight in every sync and
/// report query.
/// <para>
/// <b>Why the credential columns are nullable.</b> A row with a null <see cref="PasswordHash"/> is the
/// pre-S6 <i>legacy tenant</i>: the account S2 seeded on a self-hosted box, now carrying real projects
/// and devices but no email or password. It exists only until its owner calls
/// <c>POST /auth/claim</c>, which is the endpoint's sole purpose and this column's only writer. Such a
/// row can never authenticate — login (S7) requires a non-null hash and there is no code path that
/// treats a null one as "no password required". Once claimed, <see cref="Email"/>,
/// <see cref="NormalizedEmail"/> and <see cref="PasswordHash"/> are non-null forever; every user created
/// through <c>POST /auth/register</c> has them from birth. The unique index on
/// <see cref="NormalizedEmail"/> is therefore a Postgres <i>partial</i> index filtered to
/// <c>NormalizedEmail IS NOT NULL</c>, so unclaimed rows do not collide with each other on null.
/// </para>
/// </summary>
public sealed class User
{
    public int Id { get; set; }

    /// <summary>The address as the user typed it, kept for display. Match on <see cref="NormalizedEmail"/>.</summary>
    public string? Email { get; set; }

    /// <summary>
    /// <see cref="Email"/> upper-cased with <c>ToUpperInvariant</c>, which is what uniqueness and
    /// lookups compare on so <c>Sam@example.com</c> and <c>sam@EXAMPLE.com</c> are one account.
    /// Invariant casing, not the current culture's, because the container runs invariant-globalization.
    /// </summary>
    public string? NormalizedEmail { get; set; }

    /// <summary>PBKDF2-SHA512, salted and version-prefixed by <c>Auth.PasswordService</c>. Null = unclaimed.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// Bumped on any credential change to revoke outstanding tokens. Every token carries the value it
    /// was minted against as its <c>stamp</c> claim, and <c>TokenRevocation</c>'s
    /// <c>OnTokenValidated</c> hook rejects any token whose claim no longer matches this column — so
    /// changing this string signs every device out at once. Two endpoints write it:
    /// <c>POST /auth/password</c> (S7) and <c>POST /auth/logout-all</c> (S8). S8's other two revocations
    /// — removing a device, deleting the account — ride the same <c>OnTokenValidated</c> check without
    /// touching this column, because a missing device or user row already fails a token.
    /// </summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Consecutive failed sign-in attempts. Zeroed on a successful login <b>and</b> once
    /// <see cref="LockoutEndUtc"/> has passed — see <c>AuthEndpoints</c>' login doc for why the second
    /// of those is not optional.
    /// </summary>
    public int FailedLoginCount { get; set; }

    /// <summary>
    /// When the current lockout lifts, or null when the account is not locked. Computed <b>once</b>, at
    /// the moment the failure threshold is crossed, and only read thereafter: re-stamping it on every
    /// refused attempt would let anything that polls <c>/auth/login</c> hold the account locked forever.
    /// </summary>
    public DateTime? LockoutEndUtc { get; set; }

    /// <summary>Last successful login. Informational — nothing keys off it.</summary>
    public DateTime? LastLoginUtc { get; set; }

    /// <summary>Was <c>Account.Name</c>. Optional and free-form — nothing keys off it.</summary>
    public string? DisplayName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
