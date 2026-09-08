namespace Homespool.Host.RateLimiting;

/// <summary>
/// Every rate-limiting policy name in the application, in one place.
/// </summary>
/// <remarks>
/// <para>
/// A policy name is a string shared by three parties who cannot see each other: the
/// <c>AddPolicy</c> that registers the limits, the attribute on the endpoint that asks for them, and
/// - for the printer routes - the ceiling that reads the endpoint's metadata back. A name is
/// therefore the one thing that must not be spelled twice, and gathering them says what exists
/// without opening three files in two namespaces.
/// </para>
/// <para>
/// <b>Names only.</b> The limits themselves stay with the policy that means them, because they are
/// where their reasoning is - what a printer's window costs a stalled transfer, what a household
/// presses in a minute. A list of numbers here would be a table of magic constants with the
/// arguments somewhere else.
/// </para>
/// </remarks>
public static class RateLimitPolicies
{
    /// <summary><c>POST /p/register</c> - anonymous first contact from a printer, which mints a claim code.</summary>
    public const string PrinterRegistrationStart = "printer-registration-start";

    /// <summary><c>GET /p/register</c> - the poll a printer repeats while it waits to be claimed.</summary>
    public const string PrinterRegistrationPoll = "printer-registration-poll";

    /// <summary><c>/p/ws</c> - the upgrade a printer connects on.</summary>
    public const string PrinterSocket = "printer-socket";

    /// <summary><c>POST /p/telemetry</c> and <c>POST /p/events</c> - the pre-websocket HTTP transport.</summary>
    public const string PrinterHttpTransport = "printer-http-transport";

    /// <summary>
    /// <c>GET /p/teams/{teamId}/files/{hash}/raw</c>, and the controller-wide default every printer action
    /// inherits unless it names one of the policies above.
    /// </summary>
    public const string PrinterFile = "printer-file";

    /// <summary>Asking <c>Account/Login</c> or <c>Account/Manage/Passkeys</c> for a passkey ceremony.</summary>
    public const string PasskeyChallenge = "passkey-challenge";

    /// <summary>
    /// The anonymous pages that check a credential: <c>Login</c>, the two second-factor pages,
    /// <c>ForgotPassword</c>, <c>ResendEmailConfirmation</c> and <c>ResetPassword</c>.
    /// </summary>
    public const string SignIn = "sign-in";
}
