namespace Homespool.Host.Firmware;

/// <summary>What <see cref="PrusaFirmwareVerifier.CheckAsync"/> found.</summary>
/// <param name="Verdict">Whether the image may go to a printer, and if not, why not.</param>
/// <param name="Header">
/// What the image says about itself, whenever a header could be read - including for an image that is
/// refused, so the refusal can name the version it claimed to be.
/// </param>
public sealed record PrusaFirmwareCheck(PrusaFirmwareVerdict Verdict, PrusaFirmwareHeader? Header)
{
    /// <summary>Whether the image is intact and signed with the verifier's key.</summary>
    public bool IsVerified => Verdict == PrusaFirmwareVerdict.Verified;
}
