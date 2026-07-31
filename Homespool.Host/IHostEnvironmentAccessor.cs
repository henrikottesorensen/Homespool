namespace Homespool.Host;

/// <summary>
/// The content root, behind an interface so anything that resolves a configured relative path can be
/// constructed in a test without an <c>IWebHostEnvironment</c>.
/// </summary>
/// <remarks>
/// <para>
/// At the project root, in the root namespace, because it belongs to no feature: the file store and
/// the printer certificate authority both resolve their directories through it, and it lived under
/// <c>PrusaConnect.Transfers</c> only because the upload store happened to need it first. That left
/// <c>PrinterCertificateAuthority</c> naming a transfer namespace to reach a host concept.
/// </para>
/// <para>
/// The root namespace also means no consumer needs a <c>using</c> for it: C# searches enclosing
/// namespaces, and everything in this project is under <c>Homespool.Host</c>.
/// </para>
/// </remarks>
public interface IHostEnvironmentAccessor
{
    string ContentRootPath { get; }
}
