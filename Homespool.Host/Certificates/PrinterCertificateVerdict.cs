namespace Homespool.Host.Certificates;

/// <summary>The verdict, and the sentence an administrator will read.</summary>
/// <param name="State">What is wrong.</param>
/// <param name="Description">A full sentence, shown verbatim by <c>HealthBanner</c>.</param>
public sealed record PrinterCertificateVerdict(PrinterCertificateState State, string Description)
{
    /// <summary>Whether this is something to tell somebody about.</summary>
    public bool IsProblem => State is not (PrinterCertificateState.Ok or PrinterCertificateState.NotInUse);
}
