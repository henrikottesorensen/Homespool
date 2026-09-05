using System;

namespace Homespool.Host.Certificates;

/// <summary>
/// The Data Protection certificate cannot be handled - no passphrase is configured, or the file
/// exists and the configured passphrase does not open it - and starting anyway would mint a
/// replacement, which would leave every key already in the ring undecryptable: every session and
/// every pending reset or confirmation link, gone at once.
/// </summary>
/// <remarks>
/// Thrown instead of falling through to minting, always, on the same reasoning as
/// <see cref="CertificateAuthorityUnreadableException"/>. Every message names what could not be
/// read and the operator action that fixes it, because this surfaces at startup where the only
/// audience is a log.
/// </remarks>
public class DataProtectionCertificateUnreadableException : Exception
{
    public DataProtectionCertificateUnreadableException()
        : base("The Data Protection certificate exists but cannot be read.")
    {
    }

    public DataProtectionCertificateUnreadableException(string message)
        : base(message)
    {
    }

    public DataProtectionCertificateUnreadableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
