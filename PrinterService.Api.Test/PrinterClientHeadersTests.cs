using AwesomeAssertions;

using Microsoft.AspNetCore.Http;

using PrinterService.Api.PrusaConnect;

namespace PrinterService.Api.Test;

/// <summary>
/// Covers the header contract of the two registration endpoints, which is dictated entirely by the
/// firmware — see notes/protocol-reference.md.
/// </summary>
public class PrinterClientHeadersTests
{
    private static HttpRequest RequestWith(params (string Name, string Value)[] headers)
    {
        DefaultHttpContext context = new();

        foreach ((string name, string value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        return context.Request;
    }

    /// <summary>
    /// Buddy sends <b>no headers at all</b> on POST /p/register — the fingerprint travels in the JSON
    /// body — and only <c>Code</c> on the poll.
    /// </summary>
    /// <remarks>
    /// This is the regression guard for the bug that made enrollment impossible: the type used to
    /// throw <see cref="ArgumentNullException"/> when User-Agent-Printer, User-Agent-Version or
    /// Fingerprint was absent, and the controller turned that into a 400 on every registration.
    /// </remarks>
    [Fact]
    public void ReadingARequestWithNoHeadersAtAllDoesNotThrow()
    {
        // Act
        PrinterClientHeaders headers = new(RequestWith());

        // Assert
        headers.Printer.Should().BeNull();
        headers.FirmwareVersion.Should().BeNull();
        headers.FingerPrint.Should().BeNull();
        headers.Token.Should().BeNull();
        headers.Code.Should().BeNull();
    }

    /// <summary>
    /// The poll header is <c>Code</c>. Prusa's server emits both <c>Code</c> and
    /// <c>Temporary-Code</c> on the response, but no client ever sends <c>Temporary-Code</c> —
    /// reading it was why every poll was rejected.
    /// </summary>
    [Fact]
    public void CodeIsReadFromTheCodeHeader()
    {
        // Assert
        new PrinterClientHeaders(RequestWith(("Code", "MUF4RZJF5R")))
            .Code.Should().Be("MUF4RZJF5R");
    }

    /// <summary>
    /// A request carrying only <c>Temporary-Code</c> yields no code.
    /// </summary>
    /// <remarks>
    /// The inverse of the test above, and the one that pins down the asymmetry. Prusa's server emits
    /// both headers on the response, which is why ours does too - but no client ever sends
    /// <c>Temporary-Code</c> back. Reading it was the assumption that symmetry ran both ways.
    /// </remarks>
    [Fact]
    public void TemporaryCodeHeaderIsNotMistakenForTheCode()
    {
        // Assert
        new PrinterClientHeaders(RequestWith(("Temporary-Code", "MUF4RZJF5R")))
            .Code.Should().BeNull("no client sends Temporary-Code; only the server emits it");
    }

    /// <summary>Exactly the header set captured from a real MK3.5 polling for its token.</summary>
    [Fact]
    public void BuddysPollHeadersAreReadCorrectly()
    {
        // Act
        PrinterClientHeaders headers = new(RequestWith(
            ("User-Agent-Printer", "MK3.5"),
            ("User-Agent-Version", "6.4.0+11974"),
            ("Code", "MUF4RZJF5R")));

        // Assert
        headers.Printer.Should().Be("MK3.5");
        headers.FirmwareVersion.Should().Be("6.4.0+11974");
        headers.Code.Should().Be("MUF4RZJF5R");
        headers.FingerPrint.Should().BeNull("Buddy does not send a fingerprint during registration");
    }

    /// <summary>The Python SDK does send a fingerprint; it must be read, never required.</summary>
    [Fact]
    public void FingerprintIsReadWhenAClientDoesSendIt()
    {
        // Assert
        new PrinterClientHeaders(RequestWith(("Fingerprint", "SUDBAJQ78CTJBNA8"), ("Code", "X")))
            .FingerPrint.Should().Be("SUDBAJQ78CTJBNA8");
    }

    /// <summary>
    /// Two conflicting values for one header are treated as no value at all.
    /// </summary>
    /// <remarks>
    /// Picking the first would be a guess about which of two claimed identities is real. Returning
    /// null lets the caller reject the request instead, which for a registration code - a credential -
    /// is the safer default.
    /// </remarks>
    [Fact]
    public void RepeatedHeaderIsTreatedAsAbsentRatherThanGuessed()
    {
        // Arrange
        DefaultHttpContext context = new();
        context.Request.Headers["Code"] = new[] { "FIRST", "SECOND" };

        // Assert
        new PrinterClientHeaders(context.Request)
            .Code.Should().BeNull("picking one of two conflicting values would be a guess");
    }
}
