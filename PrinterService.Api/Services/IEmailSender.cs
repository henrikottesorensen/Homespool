using System.Threading.Tasks;

namespace PrinterService.Api.Services;

/// <summary>
/// Sends account-related email (confirmation links, password resets).
/// </summary>
/// <remarks>
/// <para>
/// This replaced <c>Microsoft.AspNetCore.Identity.UI.Services.IEmailSender</c>, which was removed along with the
/// Identity.UI package so that its default Razor pages stopped competing with the ones in Pages/Account. The
/// signature initially mirrored it so that removal did not also require rewriting every call site; that was a
/// one-off migration convenience and the contract has since been shaped around what callers actually need.
/// </para>
/// <para>
/// Failure is reported by return value rather than thrown. A mail server being unreachable is an expected condition
/// in a self-hosted deployment, not an exceptional one, and throwing risks a 500 from a page handler that has
/// already created a user row.
/// </para>
/// </remarks>
public interface IEmailSender
{
    /// <summary>Attempts to send a message, returning what happened rather than throwing.</summary>
    /// <remarks>
    /// Callers must decide what to do with a <see cref="EmailSendResult.Failed"/> result. Most should surface it -
    /// silently telling someone to check an inbox that will never receive anything is a bad failure. The exceptions
    /// are password reset and confirmation resend, where reporting it would reveal whether an account exists.
    /// </remarks>
    Task<EmailSendResult> SendEmailAsync(string email, string subject, string htmlMessage);
}
