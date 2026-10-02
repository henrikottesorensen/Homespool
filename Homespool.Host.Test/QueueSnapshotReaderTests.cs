using System;

using AwesomeAssertions;

using Homespool.Host.Queue;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="QueueSnapshotReader.StatedSinceConnecting"/> - which stored status a gate may act on.
/// </summary>
/// <remarks>
/// Against the rule directly, because what goes wrong here is a question of instants, and the
/// advancer's tests can only reach those through a seeded row and a registry on one fake clock.
/// </remarks>
public class QueueSnapshotReaderTests
{
    private static readonly DateTimeOffset Connected = DateTimeOffset.UnixEpoch.AddYears(56);

    /// <summary>A report since the connection registered is the printer speaking now.</summary>
    [Fact]
    public void AReportSinceConnectingIsActedOn()
    {
        PrinterLiveState live = Reported(PrinterStatus.Ready, Connected.AddSeconds(2));

        QueueSnapshotReader.StatedSinceConnecting(live, Connected).Should().Be(PrinterStatus.Ready);
    }

    /// <summary>A report from before the connection is not: it was said to an earlier one.</summary>
    [Fact]
    public void AReportFromBeforeTheConnectionIsUnknown()
    {
        PrinterLiveState live = Reported(PrinterStatus.Ready, Connected.AddMinutes(-5));

        QueueSnapshotReader.StatedSinceConnecting(live, Connected).Should().Be(PrinterStatus.Unknown);
    }

    /// <summary>
    /// A report received in the millisecond the connection registered counts, although its stored time
    /// reads as earlier.
    /// </summary>
    /// <remarks>
    /// <see cref="PrinterLiveState.LastSeenAt"/> comes back from the database truncated to the
    /// millisecond, so a report a fraction of a millisecond after the registration reads as before it.
    /// Seen end to end: a live state written straight after a FakePrinter connected was refused this
    /// way in five runs of six.
    /// </remarks>
    [Fact]
    public void AReportInTheRegisteringMillisecondCounts()
    {
        DateTimeOffset registered = Connected.AddTicks(4_000);
        PrinterLiveState live = Reported(PrinterStatus.Idle, Connected);

        QueueSnapshotReader.StatedSinceConnecting(live, registered).Should().Be(PrinterStatus.Idle);
    }

    /// <summary>A report from the millisecond before the connection is still from before it.</summary>
    [Fact]
    public void AReportFromTheMillisecondBeforeIsUnknown()
    {
        PrinterLiveState live = Reported(PrinterStatus.Ready, Connected.AddMilliseconds(-1));

        QueueSnapshotReader.StatedSinceConnecting(live, Connected).Should().Be(PrinterStatus.Unknown);
    }

    /// <summary>
    /// No connection, or no report ever, is unknown - whatever the stored row last said.
    /// </summary>
    [Fact]
    public void WithoutAConnectionOrAReportTheStatusIsUnknown()
    {
        QueueSnapshotReader.StatedSinceConnecting(Reported(PrinterStatus.Ready, Connected), connectedSince: null)
                           .Should().Be(PrinterStatus.Unknown, "a printer that is not here is not ready");

        QueueSnapshotReader.StatedSinceConnecting(live: null, Connected)
                           .Should().Be(PrinterStatus.Unknown, "connected is not reported");
    }

    private static PrinterLiveState Reported(PrinterStatus status, DateTimeOffset at)
    {
        return new PrinterLiveState { PrinterId = 1, Status = status, LastSeenAt = at };
    }
}
