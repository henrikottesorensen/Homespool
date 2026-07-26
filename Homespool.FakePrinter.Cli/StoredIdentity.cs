namespace Homespool.FakePrinter.Cli;

/// <summary>
/// What <c>enroll</c> writes and <c>run</c>/<c>blast</c> read back: the identity plus the token,
/// as one JSON file. The token is a credential - the file belongs next to the operator, not in
/// the repository.
/// </summary>
public sealed class StoredIdentity
{
    /// <summary>Full 50-character fingerprint.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Serial number.</summary>
    public required string SerialNumber { get; init; }

    /// <summary>Dotted printer-type code.</summary>
    public required string PrinterType { get; init; }

    /// <summary>Firmware version string.</summary>
    public required string Firmware { get; init; }

    /// <summary>The issued token; null until <c>enroll</c> completes.</summary>
    public string? Token { get; set; }

    /// <summary>Captures a wire identity for persisting.</summary>
    public static StoredIdentity From(PrinterIdentity identity, string? token)
    {
        return new StoredIdentity
        {
            Fingerprint = identity.Fingerprint,
            SerialNumber = identity.SerialNumber,
            PrinterType = identity.PrinterType,
            Firmware = identity.Firmware,
            Token = token,
        };
    }

    /// <summary>Rehydrates the wire identity.</summary>
    public PrinterIdentity ToIdentity()
    {
        return new PrinterIdentity
        {
            Fingerprint = Fingerprint,
            SerialNumber = SerialNumber,
            PrinterType = PrinterType,
            Firmware = Firmware,
        };
    }
}
