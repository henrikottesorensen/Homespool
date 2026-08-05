namespace Homespool.Host.Authorisation;

public static class Policies
{
    /// <summary>
    /// The printer-facing surface: an enrolled printer, and nothing else. Names
    /// <see cref="Authentication.Schemes.PrusaConnectPrinter"/> explicitly, so a signed-in user's
    /// cookie can never satisfy an endpoint meant for hardware.
    /// </summary>
    public const string PrusaConnectPrinter = nameof(PrusaConnectPrinter);

    /// <summary>
    /// The app API: signed in by cookie <b>or</b> holding a personal access token. Razor Pages keep
    /// the bare cookie default — there is no case for a bearer credential on an HTML form.
    /// </summary>
    public const string Api = nameof(Api);

    /// <summary>
    /// The print-host compatibility shell under <c>/print-host</c>: a personal access token in either
    /// header, and <b>never</b> the sign-in cookie. Its reasoning is in
    /// <see cref="Authentication.XApiKeyAuthenticationHandler"/> and in
    /// <see cref="Builder"/>, where the schemes are named.
    /// </summary>
    public const string PrintHost = nameof(PrintHost);
}
