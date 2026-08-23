namespace Homespool.Host.Certificates;

/// <summary>
/// How confident we are that a suggested address will keep working.
/// </summary>
public enum AddressDurability
{
    Undefined = 0,

    /// <summary>Works now and needs no DNS, but is tied to a DHCP lease.</summary>
    UntilTheLeaseMoves = 1,

    /// <summary>Survives a lease change, provided the router registers DHCP names in its own DNS.</summary>
    SurvivesALeaseChange = 2,

    /// <summary>Almost certainly wrong: a container's own address rather than the host's.</summary>
    ProbablyTheContainersOwn = 3,
}
