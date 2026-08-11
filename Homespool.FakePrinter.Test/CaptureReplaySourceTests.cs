using System;
using System.IO;
using System.Text;

using AwesomeAssertions;

namespace Homespool.FakePrinter.Test;

/// <summary>
/// The replay source's capture handling: server-to-printer command frames are stripped, the
/// printer's own documents are split one per message, and exhaustion is a clean null.
/// </summary>
public sealed class CaptureReplaySourceTests : IDisposable
{
    private readonly string _capturePath = Path.Combine(Path.GetTempPath(), $"fp-capture-{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        if (File.Exists(_capturePath))
        {
            File.Delete(_capturePath);
        }
    }

    /// <summary>
    /// A capture with two telemetry documents, one event and an interleaved command frame yields
    /// exactly the three printer-to-server documents, in order, then null.
    /// </summary>
    [Fact]
    public void CommandFramesAreStrippedAndDocumentsSplitInOrder()
    {
        File.WriteAllText(_capturePath, """
                                        {"state":"IDLE"}
                                        J00000140{"command": "SEND_INFO", "kwargs": {}}
                                        {"job_id":301,"progress":25,"state":"PRINTING"}{"state":"PRINTING","event":"STATE_CHANGED"}
                                        """);

        CaptureReplaySource source = new(_capturePath);
        FakeDevice device = new();

        source.MessageCount.Should().Be(3);
        Text(source.NextMessage(device)).Should().Be("""{"state":"IDLE"}""");
        Text(source.NextMessage(device)).Should().Be("""{"job_id":301,"progress":25,"state":"PRINTING"}""");
        Text(source.NextMessage(device)).Should().Be("""{"state":"PRINTING","event":"STATE_CHANGED"}""");
        source.NextMessage(device).Should().BeNull("the source is exhausted");
    }

    /// <summary>A capture that stops parsing mid-way fails at construction, not mid-run.</summary>
    [Fact]
    public void AMalformedCaptureFailsAtConstruction()
    {
        File.WriteAllText(_capturePath, """{"state":"IDLE"} this is not json""");

        Action act = () => _ = new CaptureReplaySource(_capturePath);

        act.Should().Throw<InvalidDataException>();
    }

    /// <summary>
    /// The real committed capture parses in full - the same 2063 documents
    /// <c>CaptureReplayTests</c> counts (2058 telemetry + 5 events).
    /// </summary>
    /// <remarks>
    /// Reads the file from the sibling test project's source tree rather than duplicating 614 KB.
    /// If the path breaks, the capture moved - update both.
    /// </remarks>
    [Fact]
    public void TheCommittedCaptureSplitsIntoTheExpectedDocumentCount()
    {
        string path = Path.Combine(FindRepositoryRoot(), "Homespool.Host.Test", "websocket.capture");

        CaptureReplaySource source = new(path);

        source.MessageCount.Should().Be(2063);
    }

    private static string Text(byte[]? payload)
    {
        payload.Should().NotBeNull();

        return Encoding.UTF8.GetString(payload!);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Homespool.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test must run from somewhere under the repository");

        return directory!.FullName;
    }
}
