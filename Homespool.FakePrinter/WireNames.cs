namespace Homespool.FakePrinter;

/// <summary>
/// Header names and content types as firmware spells them. Deliberately a copy rather than a reference
/// to the server's constants: the fake must not share the server's assumptions about the wire.
/// </summary>
internal static class WireNames
{
    /// <summary>Request header carrying the printer's fingerprint.</summary>
    public const string Fingerprint = "Fingerprint";

    /// <summary>Request header carrying the printer's token.</summary>
    public const string Token = "Token";

    /// <summary>Response header carrying the id of the command in a telemetry response.</summary>
    public const string CommandId = "Command-Id";

    /// <summary>The <c>Content-Type</c> of a command firmware parses as a gcode line.</summary>
    public const string GcodeContentType = "text/x.gcode";

    /// <summary>The older spelling of <see cref="GcodeContentType"/> that firmware also accepts.</summary>
    public const string LegacyGcodeContentType = "text/x-gcode";
}
