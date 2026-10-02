using System;
using System.Net.Security;

namespace Homespool.Host.Certificates;

/// <summary>How far a probe got.</summary>
public enum PublicCertificateProbeOutcome
{
    Undefined = 0,

    /// <summary>A certificate was served; whether a browser would accept it is in the errors.</summary>
    Served = 1,

    /// <summary>The listener answered and would not complete a handshake for the name.</summary>
    Refused = 2,

    /// <summary>Nothing answered at all.</summary>
    Unreachable = 3,
}

/// <summary>What a TLS listener served for one name.</summary>
/// <param name="Outcome">How far the probe got.</param>
/// <param name="NotAfter">The served certificate's expiry, when one was served.</param>
/// <param name="Errors">
/// What a browser's validation would object to, from the platform's own chain building against the
/// system trust store. <see cref="SslPolicyErrors.None"/> is a certificate a browser accepts.
/// </param>
/// <param name="Detail">Why nothing was served, in the platform's words, for the administrator.</param>
public sealed record PublicCertificateProbeResult(PublicCertificateProbeOutcome Outcome,
                                                  DateTimeOffset? NotAfter,
                                                  SslPolicyErrors Errors,
                                                  string? Detail)
{
    public static PublicCertificateProbeResult Served(DateTimeOffset notAfter, SslPolicyErrors errors)
    {
        return new(PublicCertificateProbeOutcome.Served, notAfter, errors, null);
    }

    public static PublicCertificateProbeResult Refused(string detail)
    {
        return new(PublicCertificateProbeOutcome.Refused, null, SslPolicyErrors.None, detail);
    }

    public static PublicCertificateProbeResult Unreachable(string detail)
    {
        return new(PublicCertificateProbeOutcome.Unreachable, null, SslPolicyErrors.None, detail);
    }
}
