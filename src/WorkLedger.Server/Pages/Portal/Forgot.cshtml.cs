using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WorkLedger.Server.Auth;
using WorkLedger.Server.Email;

namespace WorkLedger.Server.Pages.Portal;

/// <summary>
/// "Forgot your password?" (S12 item 12): anonymous, antiforgery-protected, and rate-limited with sign-in
/// (<c>PortalSetup</c>'s credential pages share the per-IP bucket). <b>It does not exist while email is
/// off</b> — both handlers answer 404, checked before anything is queued — and the sign-in page links to it
/// only while email is on. There is deliberately no JSON twin: new account features are portal-only (S11),
/// and an anonymous route that makes the server send mail wants the antiforgery check in front of it.
/// <para>
/// <b>The answer is identical whatever the address</b> — unknown, suspended, credential-less, inside the
/// cooldown, or dropped because the mail queue is full: the same page, status and markup, which never repeats
/// the address back. That is <see cref="CredentialCheck"/>'s uniform-failure rule on a new page, for the same
/// reason: an anonymous form must not say who has an account.
/// </para>
/// <para>
/// <b>And the POST does constant work.</b> It checks the field's shape, queues a
/// <see cref="ForgotPasswordRequest"/>, and renders — no lookup, no insert. Doing either here would make the
/// response time differ between known and unknown addresses, which reveals what the text does not;
/// <see cref="SelfServiceRecovery"/> does all of it later, in the delivery worker, where nobody is timing it.
/// Don't move any of that work back in here "to give a better message".
/// </para>
/// </summary>
public sealed class ForgotModel(EmailOptions email, EmailOutbox outbox, AuthOptions auth) : PageModel
{
    /// <summary>The address field's ceiling (RFC 5321's path limit), so the queue never holds a megabyte of one.</summary>
    private const int MaxEmailLength = 254;

    public int ExpiryMinutes => auth.SelfServiceResetExpiryMinutes;

    /// <summary>
    /// The address asked about. A bound property rather than a handler argument, because MVC's Trace-level
    /// "executing handler … with arguments" line would otherwise print it.
    /// </summary>
    [BindProperty]
    public string? Address { get; set; }

    /// <summary>Whether the request was taken. True for every well-formed address, account or not.</summary>
    public bool Sent { get; private set; }

    public string? Error { get; private set; }

    public IActionResult OnGet() => email.Enabled ? Page() : NotFound();

    public IActionResult OnPost()
    {
        if (!email.Enabled)
        {
            return NotFound();
        }

        var address = Address?.Trim();
        if (string.IsNullOrEmpty(address) || address.Length > MaxEmailLength || !Credentials.LooksLikeEmail(address))
        {
            // About the input's shape only, which says nothing about any account.
            Error = "Enter the email address you sign in with.";
            return Page();
        }

        // The result is ignored on purpose: a full queue answers exactly as a queued request does. The
        // ToUpperInvariant is registration's normalization, so the worker matches NormalizedEmail directly.
        outbox.TryEnqueue(new ForgotPasswordRequest(address.ToUpperInvariant()));
        Sent = true;
        return Page();
    }
}
