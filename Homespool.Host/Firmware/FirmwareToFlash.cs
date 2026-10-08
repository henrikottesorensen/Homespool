using Homespool.Host.PrintFiles;
using Homespool.Model.Entities;

namespace Homespool.Host.Firmware;

/// <summary>A stored image, verified and matched to a printer, ready to be sent and flashed.</summary>
/// <param name="Row">The image's row, which the transfer's bookkeeping is keyed on.</param>
/// <param name="File">Its bytes, under the name it takes on the printer's drive.</param>
/// <param name="Header">What it says it is - the version the printer must come back on.</param>
public sealed record FirmwareToFlash(HSFile Row, StoredFile File, PrusaFirmwareHeader Header);
