namespace Homespool.Host.Services;

/// <summary>What is wrong with how this deployment is reachable, if anything.</summary>
public enum ExposureState
{
    /// <summary>Nothing to report.</summary>
    Ok,

    /// <summary>Printers are told to dial an address the internet can reach, over plain HTTP.</summary>
    PrinterTokensCrossThePublicInternet,

    /// <summary>An administrator is using this server over plain HTTP from somewhere that is not this machine.</summary>
    SessionInClear,
}
