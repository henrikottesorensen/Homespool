namespace Homespool.Host.PrusaConnect.Commands;

public enum PropertyName
{
    Undefined = 0,
    HostName = 1,
    EnclosureEnabled = 2,
    EnclosurePrintingFiltration = 3,
    EnclosurePostPrint = 4,
    EnclosurePostPrintFiltrationTime = 5,
    NozzleDiameter = 6,
    NozzleHighFlow = 7,
    NozzleHardened = 8,
    ChamberTargetTemp = 9,
    ChamberFanPwmTarget = 10,
    AddonPower = 11, // not a very descriptive name, but the Connect team understands this property name as USB power output on the XBE
    ChamberLedIntensity = 12,
}
