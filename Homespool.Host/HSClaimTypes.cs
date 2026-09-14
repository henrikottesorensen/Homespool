namespace Homespool.Host;

public static class HSClaimTypes
{
    /// <summary>
    /// The printer's surrogate key. This is the internal <c>Printer.Id</c>, not the public
    /// <c>Uuid</c> — the principal never leaves the server, and dispatch needs the value that
    /// telemetry rows are keyed by.
    /// </summary>
    public const string PrinterId = "printer-id";

    public const string Owner = "owner";

    /// <summary>
    /// When an external provider says the person signed in there: the provider's own <c>auth_time</c>,
    /// moved aside as its answer arrives, because <c>auth_time</c> on an external principal is this
    /// server's - when that answer came back. <c>ExternalSignIn.RestateAuthenticationTime</c> writes both.
    /// </summary>
    public const string ExternalAuthenticationTime = "external_auth_time";
}
