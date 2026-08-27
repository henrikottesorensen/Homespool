using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.PrusaConnect.DTO.Transfers;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Host.Telemetry;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// How <see cref="PrinterConnectionActor"/> answers the printer's range requests - which transfers it
/// serves, which it refuses, and how it refuses them.
/// </summary>
/// <remarks>
/// The refusals matter as much as the successes here, and in an unusual way: firmware's inline engine
/// carries no stall timeout of any kind (<c>Download::Inline</c>, download.hpp:138-151), so a request
/// that goes unanswered leaves the printer waiting indefinitely, mid-print. Every path through the
/// handler therefore has to end in either a chunk or a deliberate zero-length chunk - the "error
/// indicated by server" signal (download.cpp:556-577) - and never in silence.
/// </remarks>
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                 Justification =
                     "Content is handed to the actor, which owns disposal - on a terminal event or at teardown. That transfer of ownership is itself what two of these tests assert.")]
public class TransferRequestHandlingTests
{
    private const string Hash = "offer-hash";

    /// <summary>
    /// The case that plain gcode always produces, and that a first implementation got wrong. A
    /// <c>RangeJump</c> - which every plain gcode file over 512 KiB performs, because the preview
    /// metadata sits at the end and has to be fetched before the body - restarts the download with a
    /// <b>fresh random <c>file_id</c></b> and re-sends the hash (transfer.cpp:161-223). Reading that
    /// as a different transfer and ignoring it would hang the print.
    /// </summary>
    [Fact]
    public async Task ARangeJumpRenegotiatesTheFileIdAndIsStillServed()
    {
        // Arrange
        RecordingConnection connection = new();
        PrinterConnectionActor actor = NewActor(connection, new ArrayContent(new byte[2048]));

        // Act
        // The tail first, as PlainGcodeDownloadOrder does...
        await Post(actor, new InlineRequestDTO { FileId = 111, Start = 1024, End = 2047, Hash = Hash });
        await WaitUntilAsync(() => connection.Chunks.Count == 1);

        // ...then the jump to the body: new file_id, hash re-sent, same content.
        await Post(actor, new InlineRequestDTO { FileId = 222, Start = 0, End = 1023, Hash = Hash });
        await WaitUntilAsync(() => connection.Chunks.Count == 2);

        // Assert
        connection.Chunks[0].Should().Be((111u, 1024L, 1024L));
        connection.Chunks[1].Should().Be((222u, 0L, 1024L),
                                         "the jump is the same transfer renegotiated, not a foreign one");
        connection.EmptyChunks.Should().BeEmpty("nothing here is a failure");
    }

    /// <summary>
    /// Continuation requests carry no hash and are matched on <c>file_id</c> alone - that is the
    /// steady state, and by far the most common message.
    /// </summary>
    [Fact]
    public async Task AContinuationRequestWithoutAHashIsServed()
    {
        // Arrange
        RecordingConnection connection = new();
        PrinterConnectionActor actor = NewActor(connection, new ArrayContent(new byte[2048]));

        // Act
        await Post(actor, new InlineRequestDTO { FileId = 7, Start = 0, End = 1023, Hash = Hash });
        await WaitUntilAsync(() => connection.Chunks.Count == 1);
        await Post(actor, new InlineRequestDTO { FileId = 7, Start = 1024, End = 2047 });
        await WaitUntilAsync(() => connection.Chunks.Count == 2);

        // Assert
        connection.Chunks[1].Should().Be((7u, 1024L, 1024L));
    }

    /// <summary>
    /// A hash we do not recognise - a printer resuming across a server restart, most likely. It gets
    /// the failure signal rather than silence, so it gives up instead of waiting forever.
    /// </summary>
    [Fact]
    public async Task AnUnknownHashIsFailedRatherThanIgnored()
    {
        // Arrange
        RecordingConnection connection = new();
        PrinterConnectionActor actor = NewActor(connection, content: (ITransferContent?)null);

        // Act
        await Post(actor, new InlineRequestDTO { FileId = 9, Start = 0, End = 99, Hash = "never-offered" });
        await WaitUntilAsync(() => connection.EmptyChunks.Count == 1);

        // Assert
        connection.EmptyChunks.Should().Equal(9u);
        connection.Chunks.Should().BeEmpty();
    }

    /// <summary>
    /// A range past the end of the file. Serving a short chunk would be the worst outcome available -
    /// firmware would wait for the remainder forever - so this fails the transfer instead.
    /// </summary>
    [Fact]
    public async Task ARangeBeyondTheFileIsFailedRatherThanTruncated()
    {
        // Arrange
        RecordingConnection connection = new();
        PrinterConnectionActor actor = NewActor(connection, new ArrayContent(new byte[100]));

        // Act
        await Post(actor, new InlineRequestDTO { FileId = 3, Start = 0, End = 999, Hash = Hash });
        await WaitUntilAsync(() => connection.EmptyChunks.Count == 1);

        // Assert
        connection.Chunks.Should().BeEmpty();
        connection.EmptyChunks.Should().Equal(3u);
    }

    /// <summary>
    /// A finished transfer releases its content. Without this the file handle would live as long as
    /// the connection, and printers stay connected for weeks.
    /// </summary>
    [Fact]
    public async Task ATerminalTransferEventReleasesTheContent()
    {
        // Arrange
        RecordingConnection connection = new();
        TrackedContent content = new(new byte[512]);
        PrinterConnectionActor actor = NewActor(connection, content);

        await Post(actor, new InlineRequestDTO { FileId = 5, Start = 0, End = 511, Hash = Hash, TransferId = 4242 });
        await WaitUntilAsync(() => connection.Chunks.Count == 1);

        // Act
        await actor.PostAsync(
            new InboundEventMessage(DateTimeOffset.UtcNow, new EventDTO
            {
                Status = "IDLE",
                EventType = PrinterEventType.TransferFinished,
                TransferId = 4242,
            }),
            CancellationToken.None);

        // Assert
        await WaitUntilAsync(() => content.Disposed);
    }

    /// <summary>
    /// A terminal event for someone else's transfer - a PrusaLink upload finishing, say - must not
    /// release ours. The printer's transfer slot is shared between the two kinds, so the event types
    /// are identical and only the id distinguishes them.
    /// </summary>
    [Fact]
    public async Task ATerminalEventForADifferentTransferLeavesOursOpen()
    {
        // Arrange
        RecordingConnection connection = new();
        TrackedContent content = new(new byte[512]);
        PrinterConnectionActor actor = NewActor(connection, content);

        await Post(actor, new InlineRequestDTO { FileId = 5, Start = 0, End = 511, Hash = Hash, TransferId = 4242 });
        await WaitUntilAsync(() => connection.Chunks.Count == 1);

        // Act
        await actor.PostAsync(
            new InboundEventMessage(DateTimeOffset.UtcNow, new EventDTO
            {
                Status = "IDLE",
                EventType = PrinterEventType.TransferFinished,
                TransferId = 9999,
            }),
            CancellationToken.None);

        // A second request, so the assertion below waits on something that actually happened rather
        // than on a fixed delay.
        await Post(actor, new InlineRequestDTO { FileId = 5, Start = 0, End = 511 });
        await WaitUntilAsync(() => connection.Chunks.Count == 2);

        // Assert
        content.Disposed.Should().BeFalse();
    }

    /// <summary>
    /// A read that never returns must not wedge the actor. This is the hazard of file I/O on the
    /// loop, and the reason the read
    /// goes through an awaitable API at all: the wait can be bounded, and on expiry the actor gives up
    /// on the <i>connection</i> rather than just the chunk, exactly as a stalled socket write does.
    /// </summary>
    /// <remarks>
    /// A network mount that stops responding is the real-world shape of this, and it is precisely the
    /// case a memory-mapped file would have made unbounded and undetectable.
    /// </remarks>
    [Fact]
    public async Task AReadThatNeverCompletesAbandonsTheConnection()
    {
        // Arrange
        RecordingConnection connection = new();
        ITransferContentStore store = Substitute.For<ITransferContentStore>();

        store.TryOpen(Hash, out Arg.Any<ITransferContent?>()).Returns(call =>
        {
            call[1] = new NeverReturningContent();

            return true;
        });

        // The real connection's chunk send, so the read actually happens rather than being recorded.
        PrinterConnectionActor actor = new(1, new ReadingConnection(), Substitute.For<ITelemetrySink>(),
                                           NullLogger<PrinterConnectionActor>.Instance, TimeSpan.FromSeconds(10), store)
        {
            SendTimeout = TimeSpan.FromMilliseconds(200),
        };

        // Act
        await Post(actor, new InlineRequestDTO { FileId = 1, Start = 0, End = 511, Hash = Hash });

        // Assert
        // Abandoning the connection is the actor completing its own mailbox, which is what makes the
        // read loop exit and the socket get disposed.
        await actor.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private static async Task Post(PrinterConnectionActor actor, InlineRequestDTO request)
    {
        await actor.PostAsync(new InboundTransferRequestMessage(DateTimeOffset.UtcNow, request), CancellationToken.None);
    }

    private static PrinterConnectionActor NewActor(IPrinterConnection connection, ITransferContent? content)
    {
        ITransferContentStore store = Substitute.For<ITransferContentStore>();

        store.TryOpen(Hash, out Arg.Any<ITransferContent?>()).Returns(call =>
        {
            call[1] = content;

            return content is not null;
        });

        return new PrinterConnectionActor(1, connection, Substitute.For<ITelemetrySink>(),
                                          NullLogger<PrinterConnectionActor>.Instance, TimeSpan.FromSeconds(10), store);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);

        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        condition().Should().BeTrue("the actor should have processed the request within 5s");
    }

    /// <summary>
    /// Records what was sent rather than sending it: chunk sends as (file id, offset, count), and
    /// bare header writes - the failure signal - as their file id.
    /// </summary>
    private sealed class RecordingConnection : IChunkStreamingConnection
    {
        public List<(uint fileId, long offset, long count)> Chunks { get; } = [];

        public List<uint> EmptyChunks { get; } = [];

        public bool IsOpen => true;

        public ValueTask<CommandHandover> SendCommandAsync(uint commandId, ISendableCommand command, CancellationToken cancellationToken)
        {
            // Nothing in these tests sends a command; recording nothing keeps a stray one visible as
            // an absent chunk rather than a mis-filed one.
            return ValueTask.FromResult(CommandHandover.Written);
        }

        public PendingCommand? TakeParkedCommand()
        {
            return null;
        }

        public ValueTask SendEmptyChunkAsync(ReadOnlyMemory<byte> header, CancellationToken cancellationToken)
        {
            EmptyChunks.Add(ParseFileId(header.Span));

            return ValueTask.CompletedTask;
        }

        public ValueTask SendChunkAsync(ReadOnlyMemory<byte> header,
                                        ITransferContent content,
                                        long offset,
                                        long count,
                                        CancellationToken cancellationToken)
        {
            Chunks.Add((ParseFileId(header.Span), offset, count));

            return ValueTask.CompletedTask;
        }

        private static uint ParseFileId(ReadOnlySpan<byte> header)
        {
            return uint.Parse(System.Text.Encoding.ASCII.GetString(header[1..9]),
                              System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Reports whether it was disposed, so the release-on-completion path is observable.</summary>
    private sealed class TrackedContent(byte[] bytes) : ITransferContent
    {
        public bool Disposed { get; private set; }

        public long Length => bytes.Length;

        public ValueTask<int> ReadAsync(Memory<byte> destination, long offset, CancellationToken cancellationToken)
        {
            int count = Math.Min(destination.Length, (int)(bytes.Length - offset));
            bytes.AsSpan((int)offset, count).CopyTo(destination.Span);

            return ValueTask.FromResult(count);
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    /// <summary>A disk that has stopped answering - a hung network mount, in effect.</summary>
    private sealed class NeverReturningContent : ITransferContent
    {
        public long Length => long.MaxValue;

        public ValueTask<int> ReadAsync(Memory<byte> destination, long offset, CancellationToken cancellationToken)
        {
            return new(new TaskCompletionSource<int>().Task);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Actually pulls from the content, unlike <see cref="RecordingConnection"/> - needed by the
    /// stalled-read case, where the point is that the read is reached at all.
    /// </summary>
    private sealed class ReadingConnection : IChunkStreamingConnection
    {
        public bool IsOpen => true;

        public ValueTask<CommandHandover> SendCommandAsync(uint commandId, ISendableCommand command, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(CommandHandover.Written);
        }

        public PendingCommand? TakeParkedCommand()
        {
            return null;
        }

        public ValueTask SendEmptyChunkAsync(ReadOnlyMemory<byte> header, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public async ValueTask SendChunkAsync(ReadOnlyMemory<byte> header,
                                              ITransferContent content,
                                              long offset,
                                              long count,
                                              CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[count];
            await content.ReadAsync(buffer, offset, cancellationToken);
        }
    }

    private sealed class ArrayContent(byte[] bytes) : ITransferContent
    {
        public long Length => bytes.Length;

        public ValueTask<int> ReadAsync(Memory<byte> destination, long offset, CancellationToken cancellationToken)
        {
            int count = Math.Min(destination.Length, (int)(bytes.Length - offset));
            bytes.AsSpan((int)offset, count).CopyTo(destination.Span);

            return ValueTask.FromResult(count);
        }

        public void Dispose()
        {
        }
    }
}
