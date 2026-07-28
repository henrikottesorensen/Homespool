namespace Homespool.Host.Authentication;

public static class Schemes
{
    /// <summary>
    /// Printers on the <c>/p</c> endpoints, identified by the <c>Fingerprint</c> and <c>Token</c>
    /// headers their firmware sends on every request and on the WebSocket upgrade. See
    /// <see cref="PrusaConnectPrinterAuthenticationHandler"/>.
    /// </summary>
    public const string PrusaConnectPrinter = "prusaConnect";

    /// <summary>Personal access tokens on the app API. See <see cref="ApiTokenAuthenticationHandler"/>.</summary>
    public const string ApiToken = "apiToken";
}
