using System.Threading.Tasks;

namespace PrinterService.Api.Services;

/// <summary>
/// Sends account-related email (confirmation links, password resets).
/// </summary>
/// <remarks>
/// This replaces <c>Microsoft.AspNetCore.Identity.UI.Services.IEmailSender</c>, which was removed along with the
/// Identity.UI package so that its default Razor pages stop competing with the ones in Pages/Account.
/// The signature is deliberately identical, so the existing page models bind to it unchanged.
/// </remarks>
public interface IEmailSender
{
    Task SendEmailAsync(string email, string subject, string htmlMessage);
}
