namespace Homespool.Host.Services;

/// <summary>What is wrong with how this deployment is reachable, if anything.</summary>
public enum ExposureState
{
    Undefined = 0,

    /// <summary>Nothing to report.</summary>
    Ok = 1,

    /// <summary>Printers are told to use an address the internet can reach, over plain HTTP.</summary>
    PrinterTokensCrossThePublicInternet = 2,

    /// <summary>An administrator is using this server over plain HTTP from somewhere that is not this machine.</summary>
    SessionInClear = 3,
}
