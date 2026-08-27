namespace Homespool.Host.DTO;

/// <summary>
/// The answer to a slicer's version probe. Serialised camelCase, so the members on the wire are
/// <c>api</c> and <c>server</c> — which is what the client looks for.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is deliberately no <c>text</c> member</b>, and that absence is the reason the OctoPrint
/// host type was chosen over PrusaLink's. PrusaSlicer's OctoPrint validator accepts a missing
/// <c>text</c> and only checks it when present, where <c>PrusaLink::validate_version_text</c>
/// <em>rejects</em> a missing one and requires it to name PrusaLink or OctoPrint
/// (<c>docs/prusa-slicer-integration.md</c> §2.3, §3.1). So this is the one compatible answer that
/// claims to be nobody.
/// </para>
/// <para>
/// <see cref="Api"/> is the API level the client expects to find and reads no further into;
/// <see cref="Server"/> is free-form and is ours to fill.
/// </para>
/// </remarks>
public class OctoPrintVersionDTO
{
    /// <summary>
    /// The API level. <c>0.1</c> is what both real hosts report, and the client only requires the
    /// member to exist.
    /// </summary>
    public string Api { get; set; } = "0.1";

    /// <summary>This server's version, for a human reading a log. Nothing parses it.</summary>
    public string Server { get; set; } = "1.0.0";
}
