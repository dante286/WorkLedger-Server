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
    /// <see cref="LockoutEndUtc"/> has passed — see <c>CredentialCheck</c> for why the second
    /// of those is not optional.
    /// </summary>
    public int FailedLoginCount { get; set; }

    /// <summary>
    /// When the current lockout lifts, or null when the account is not locked. Computed <b>once</b>, at
    /// the moment the failure threshold is crossed, and only read thereafter: re-stamping it on every
    /// refused attempt would let anything that polls <c>/auth/login</c> hold the account locked forever.
    /// </summary>
    public DateTime? LockoutEndUtc { get; set; }

    /// <summary>
    /// Last successful sign-in, of either kind: a device's <c>POST /auth/login</c> or a browser's portal
    /// sign-in (S11). Both go through <c>CredentialCheck</c>, which is what writes this. It is a user-level
    /// fact about the person signing in, not about any one device, so the portal's lack of a device row
    /// does not exempt it. Informational — nothing keys off it; the admin user list shows it.
    /// </summary>
    public DateTime? LastLoginUtc { get; set; }

    /// <summary>Was <c>Account.Name</c>. Optional and free-form — nothing keys off it.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Whether this user may call <c>/admin/*</c> (S10). A bool rather than a role enum: there are two
    /// levels and no prospect of a third, and an enum would only invite someone to invent a category
    /// nothing enforces.
    /// <para>
    /// <b>This is never carried in a token.</b> Tokens here are long-lived (<c>Jwt:ExpiryDays</c>,
    /// default 365), so a privilege minted into one would outlive any decision to withdraw it.
    /// <c>TokenRevocation</c> reads this column on every authenticated request — it already loads the
    /// row, so this costs nothing — and emits the <c>admin</c> claim onto the validated principal
    /// itself. A demotion therefore takes effect within <c>Auth:StampCacheSeconds</c> like every other
    /// revocation, and the demoting endpoint evicts the cache key so in practice it is immediate.
    /// </para>
    /// <para>
    /// <b>How the first one comes to exist.</b> On an empty database the first <c>POST /auth/register</c>
    /// creates an admin; on a deployment that already had users when S10 landed, they are all non-admin
    /// and <c>POST /admin/bootstrap</c> promotes the first caller who can present the signup secret.
    /// <b>The last remaining admin cannot be demoted, disabled or deleted</b> — see
    /// <c>AdminOperations</c> — because a deployment with none is one only <c>psql</c> can repair.
    /// </para>
    /// </summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// When an admin suspended this account, or null for an active one (S10). Suspension is reversible
    /// and keeps every row; <c>DELETE /admin/users/{id}</c> is the irreversible one.
    /// <para>
    /// <b>A timestamp rather than a bool</b> so the audit question "since when" is answerable from the
    /// row itself, which a bool would throw away for no saving.
    /// </para>
    /// <para>
    /// It revokes access through two separate paths, and both are needed: <c>TokenRevocation</c> fails
    /// every outstanding token while this is set (so existing sessions die on their next request), and
    /// <c>POST /auth/login</c> refuses to mint a new one. Login's refusal is the <b>same 401 as a wrong
    /// password</b> — a distinguishable "this account is suspended" would hand an anonymous caller a
    /// fact about an account that exists, which is precisely what login's uniform 401 is for.
    /// </para>
    /// </summary>
    public DateTime? DisabledAtUtc { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
