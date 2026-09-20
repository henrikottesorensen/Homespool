using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Enums;

namespace Homespool.Host.Test;

/// <summary>
/// Covers <see cref="PrinterWireComplaints"/>: what a printer can make the application log say, and
/// how often.
/// </summary>
/// <remarks>
/// Every line here is one whoever holds a printer's token can ask for, on the socket as fast as it
/// will carry messages. So the assertions are about quantity as much as content: a burst is one
/// line, not a hundred, and nothing on it is a stack trace.
/// </remarks>
public class PrinterWireComplaintsTests
{
    private static readonly JsonException Unreadable = new("'x' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0.");

    private static List<FakeLogRecord> Lines(FakeLogger<PrinterWireComplaints> logger)
    {
        return logger.Collector.GetSnapshot().ToList();
    }

    private static string? Property(FakeLogRecord record, string name)
    {
        return record.StructuredState!.FirstOrDefault(pair => pair.Key == name).Value;
    }

    [Fact]
    public void ABurstIsOneLine()
    {
        // Arrange
        FakeLogger<PrinterWireComplaints> logger = new();
        PrinterWireComplaints complaints = new(logger) { Interval = TimeSpan.FromMinutes(10) };

        // Act
        for (int i = 0; i < 100; i++)
        {
            complaints.Refused(7, WireComplaint.UnreadableMessage, Unreadable);
        }

        // Assert
        FakeLogRecord line = Lines(logger).Should().ContainSingle().Subject;

        line.Level.Should().Be(LogLevel.Warning, "a printer sending garbage is the printer's fault, not an error of ours");
        Property(line, "PrinterId").Should().Be("7");
        Property(line, "Detail").Should().StartWith("'x' is an invalid start of a value");
    }

    /// <summary>
    /// What was not logged is not lost: the next line that is let through carries the count of
    /// everything since the last one, and the running total.
    /// </summary>
    [Fact]
    public async Task TheNextLineCarriesTheCountOfWhatWasNotLogged()
    {
        // Arrange
        FakeLogger<PrinterWireComplaints> logger = new();
        PrinterWireComplaints complaints = new(logger) { Interval = TimeSpan.FromMilliseconds(50) };

        for (int i = 0; i < 100; i++)
        {
            complaints.Refused(7, WireComplaint.UnreadableMessage, Unreadable);
        }

        int linesAfterTheBurst = Lines(logger).Count;

        // Act
        await Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken);

        complaints.Refused(7, WireComplaint.UnreadableMessage, Unreadable);

        // Assert
        List<FakeLogRecord> lines = Lines(logger);

        lines.Should().HaveCount(linesAfterTheBurst + 1, "the window had closed, so this one is let through");

        // Summed rather than read off one line: on a machine slow enough for the burst to straddle a
        // window there are more summaries, and what must hold is that between them nothing is lost.
        lines.Skip(1).Sum(line => long.Parse(Property(line, "Count")!, System.Globalization.CultureInfo.InvariantCulture))
             .Should().Be(100, "the 99 that were not logged, and this one");

        Property(lines[^1], "Total").Should().Be("101");
    }

    /// <summary>
    /// A printer making noise cannot use up the line that would have named a different one, nor the
    /// line for a different complaint about itself.
    /// </summary>
    [Fact]
    public void EachPrinterAndEachComplaintHasItsOwnThrottle()
    {
        // Arrange
        FakeLogger<PrinterWireComplaints> logger = new();
        PrinterWireComplaints complaints = new(logger) { Interval = TimeSpan.FromMinutes(10) };

        for (int i = 0; i < 100; i++)
        {
            complaints.Refused(7, WireComplaint.UnreadableMessage, Unreadable);
        }

        // Act
        complaints.Refused(8, WireComplaint.UnreadableMessage, Unreadable);
        complaints.Refused(7, WireComplaint.UnreadableInfo, Unreadable);

        // Assert
        Lines(logger).Select(line => (Property(line, "PrinterId"), Property(line, "Complaint"))).Should().Equal(
            ("7", "sent a message that could not be read"),
            ("8", "sent a message that could not be read"),
            ("7", "sent an INFO event whose data could not be read"));
    }

    /// <summary>
    /// The exception's message is logged and the exception is not: the throw site is always the
    /// parser, so a stack trace adds forty lines and no fact. And the message is cleaned and cut,
    /// because a parser quotes the bytes it choked on, and those are the sender's to choose.
    /// </summary>
    [Fact]
    public void TheMessageIsLoggedCleanedAndTheExceptionIsNot()
    {
        // Arrange - an escape sequence, a newline and a bidi override, then far too much of it
        FakeLogger<PrinterWireComplaints> logger = new();
        PrinterWireComplaints complaints = new(logger);

        JsonException hostile = new("'\u001B[2J\nforged line\u202E' is invalid" + new string('x', 500));

        // Act
        complaints.Refused(7, WireComplaint.UnreadableMessage, hostile);

        // Assert
        FakeLogRecord line = Lines(logger).Should().ContainSingle().Subject;

        line.Exception.Should().BeNull();

        string detail = Property(line, "Detail")!;

        detail.Should().StartWith("'\uFFFD[2J\uFFFDforged line\uFFFD' is invalid");
        detail.Should().EndWith(" characters in all>");
        detail.Length.Should().BeLessThan(260);
    }

    /// <summary>
    /// A refusal that is ours to word goes through the same throttle as one a parser worded, under
    /// its own complaint - so it neither floods nor uses up another complaint's line.
    /// </summary>
    [Theory]
    [InlineData(WireComplaint.BodyTooLarge, "posted a body over the size ceiling")]
    [InlineData(WireComplaint.InlineTransferOverHttp, "requested an inline transfer chunk over HTTP, which cannot be served")]
    public void ARefusalInOurOwnWordsIsThrottledToo(WireComplaint complaint, string said)
    {
        // Arrange
        FakeLogger<PrinterWireComplaints> logger = new();
        PrinterWireComplaints complaints = new(logger) { Interval = TimeSpan.FromMinutes(10) };

        // Act
        for (int i = 0; i < 100; i++)
        {
            complaints.Refused(7, complaint, "the particulars");
        }

        complaints.Refused(7, WireComplaint.UnreadableMessage, Unreadable);

        // Assert
        List<FakeLogRecord> lines = Lines(logger);

        lines.Select(line => Property(line, "Complaint")).Should().Equal(said, "sent a message that could not be read");
        Property(lines[0], "Detail").Should().Be("the particulars");
        lines.Should().OnlyContain(line => line.Level == LogLevel.Warning && line.Exception == null);
    }

    [Fact]
    public void AMendedMessageSaysHowManyAndWhichSpellings()
    {
        // Arrange
        FakeLogger<PrinterWireComplaints> logger = new();
        PrinterWireComplaints complaints = new(logger);

        // Act
        complaints.Mended(7, [new NonFiniteToken(2, "nan"), new NonFiniteToken(3, "-inf"), new NonFiniteToken(4, "nan")]);

        // Assert
        FakeLogRecord line = Lines(logger).Should().ContainSingle().Subject;

        line.Level.Should().Be(LogLevel.Warning);
        Property(line, "PrinterId").Should().Be("7");
        Property(line, "Complaint").Should().Be("sent non-finite numbers that are not JSON");
        Property(line, "Detail").Should().Be("3 in this message: nan -inf");
    }
}
