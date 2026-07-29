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
}
