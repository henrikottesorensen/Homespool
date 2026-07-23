namespace PrinterService.Host.PrusaConnect.Commands;

public enum PropertyName
{
    HostName,
    EnclosureEnabled,
    EnclosurePrintingFiltration,
    EnclosurePostPrint,
    EnclosurePostPrintFiltrationTime,
    NozzleDiameter,
    NozzleHighFlow,
    NozzleHardened,
    ChamberTargetTemp,
    ChamberFanPwmTarget,
    AddonPower, // not a very descriptive name, but the Connect team understands this property name as USB power output on the XBE
    ChamberLedIntensity,
}
