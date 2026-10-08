namespace Homespool.Host.Firmware;

/// <summary>Why an upload was not stored as a firmware image.</summary>
public enum FirmwareImageRefusal
{
    /// <summary>Never set. The zero value every enum here reserves for "nobody wrote this".</summary>
    Undefined = 0,

    /// <summary>The name does not end in <c>.bbf</c>.</summary>
    NotAnImageName = 1,

    /// <summary>The verifier did not find an intact, Prusa-signed image; the verdict says which.</summary>
    NotVerified = 2,

    /// <summary>The image is built for another kind of printer.</summary>
    WrongPrinter = 3,

    /// <summary>The printer has not yet said what it is, so nothing can be matched to it.</summary>
    PrinterModelUnknown = 4,

    /// <summary>Another image is already stored under this name by the same person.</summary>
    NameTaken = 5,
}
