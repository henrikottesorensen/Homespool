namespace Homespool.Host.Certificates;

/// <summary>
/// What has gone wrong between the printer certificate and the machine it belongs to, if anything.
/// </summary>
public enum PrinterCertificateState
{
    Undefined = 0,

    /// <summary>The certificate covers what printers are told to use, and is not near expiry.</summary>
    Ok = 1,

    /// <summary>Printers use plain HTTP, so there is no certificate and nothing to check.</summary>
    NotInUse = 2,

    /// <summary>No certificate has been issued at all.</summary>
    Missing = 3,

    /// <summary>The address printers are told to use is absent from the certificate.</summary>
    ConfiguredAddressUncovered = 4,

    /// <summary>Every address the certificate names has gone from this machine.</summary>
    AddressesMoved = 5,

    /// <summary>The leaf is close to expiring — a restart's worth of work.</summary>
    LeafExpiring = 6,

    /// <summary>The authority is close to expiring — a USB visit to every printer.</summary>
    AuthorityExpiring = 7,

    /// <summary>The address printers are told to use is longer than a printer's field, so they dial a truncated one.</summary>
    ConfiguredAddressTooLong = 8,

    /// <summary>
    /// The address printers are told to use resolves only to loopback from inside this container, so
    /// detection cannot see which of this machine's addresses answer.
    /// </summary>
    ConfiguredAddressResolvesToLoopback = 9,
}
