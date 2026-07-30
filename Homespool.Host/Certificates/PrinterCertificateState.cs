namespace Homespool.Host.Certificates;

/// <summary>
/// What has gone wrong between the printer certificate and the machine it belongs to, if anything.
/// </summary>
public enum PrinterCertificateState
{
    /// <summary>The certificate covers what printers are told to dial, and is not near expiry.</summary>
    Ok,

    /// <summary>Printers use plain HTTP, so there is no certificate and nothing to check.</summary>
    NotInUse,

    /// <summary>No certificate has been issued at all.</summary>
    Missing,

    /// <summary>The address printers are told to dial is absent from the certificate.</summary>
    ConfiguredAddressUncovered,

    /// <summary>Every address the certificate names has gone from this machine.</summary>
    AddressesMoved,

    /// <summary>The leaf is close to expiring — a restart's worth of work.</summary>
    LeafExpiring,

    /// <summary>The authority is close to expiring — a USB visit to every printer.</summary>
    AuthorityExpiring,
}
