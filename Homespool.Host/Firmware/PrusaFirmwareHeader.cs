using System.Globalization;

namespace Homespool.Host.Firmware;

/// <summary>What a Prusa firmware image says about itself in its header.</summary>
/// <param name="Major">The version's major part.</param>
/// <param name="Minor">The version's minor part.</param>
/// <param name="Patch">The version's patch part.</param>
/// <param name="Build">The build number, which Prusa writes after a <c>+</c>.</param>
/// <param name="Prerelease">The prerelease label - <c>RC1</c>, <c>BETA2</c> - or empty for a release.</param>
/// <param name="Board">The main board's major version.</param>
/// <param name="PrinterType">
/// The printer family, as firmware's build numbers it: <c>1</c> for the MK4 family, <c>7</c> the Core
/// One, <c>8</c> the Core One L. A family number, never a model name.
/// </param>
/// <param name="PrinterVersion">The version within the family: <c>10</c> is an INDX build.</param>
/// <param name="PrinterSubversion">The subversion within the family: <c>5</c> on an MK3.5.</param>
/// <param name="FirmwareLength">The length of the firmware image after the header, in bytes.</param>
public sealed record PrusaFirmwareHeader(int Major,
                                         int Minor,
                                         int Patch,
                                         int Build,
                                         string Prerelease,
                                         int Board,
                                         int PrinterType,
                                         int PrinterVersion,
                                         int PrinterSubversion,
                                         long FirmwareLength)
{
    /// <summary>
    /// The version as Prusa writes it: <c>7.0.0+16903</c>, or <c>6.5.0-RC1+11234</c> for a prerelease.
    /// </summary>
    public string Version => Prerelease.Length == 0 ?
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}+{Build}") :
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{Prerelease}+{Build}");
}
