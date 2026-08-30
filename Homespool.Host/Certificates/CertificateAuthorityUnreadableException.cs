using System;

namespace Homespool.Host.Certificates;

/// <summary>
/// The authority's key material exists but cannot be read, and starting anyway would eventually mint
/// a replacement authority — which would strand every provisioned printer until each is visited with
/// a USB stick.
/// </summary>
/// <remarks>
/// Thrown instead of falling through to minting, always. Every message names the file it could not
/// read and the operator action that fixes it, because this surfaces at startup where the only
/// audience is a log.
/// </remarks>
public class CertificateAuthorityUnreadableException : Exception
{
    public CertificateAuthorityUnreadableException()
        : base("The printer certificate authority's key material exists but cannot be read.")
    {
    }

    public CertificateAuthorityUnreadableException(string message)
        : base(message)
    {
    }

    public CertificateAuthorityUnreadableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
