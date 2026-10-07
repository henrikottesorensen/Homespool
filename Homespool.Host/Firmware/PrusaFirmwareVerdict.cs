namespace Homespool.Host.Firmware;

/// <summary>What checking a file as a Prusa firmware image found.</summary>
/// <remarks>
/// Only <see cref="Verified"/> may reach a printer. The others are kept apart because each says
/// something different to the person holding the file: the wrong file, a damaged download, a local
/// build, or something pretending to be Prusa's.
/// </remarks>
public enum PrusaFirmwareVerdict
{
    /// <summary>Never set. The zero value every enum here reserves for "nobody wrote this".</summary>
    Undefined = 0,

    /// <summary>Intact, and signed with the key the image's verifier holds.</summary>
    Verified = 1,

    /// <summary>
    /// Not a firmware image the verifier can read: too short for the header, or a header version other
    /// than the one every current Buddy image uses.
    /// </summary>
    NotAnImage = 2,

    /// <summary>The file ends before the firmware the header describes.</summary>
    Truncated = 3,

    /// <summary>
    /// The header's digest does not match the bytes it covers: the file was damaged, or changed after
    /// it was built.
    /// </summary>
    Damaged = 4,

    /// <summary>
    /// Intact but carrying no signature at all, which is what a locally built image looks like.
    /// </summary>
    NoSignature = 5,

    /// <summary>Intact, and signed - but not with the key the verifier holds.</summary>
    SignatureInvalid = 6,
}
