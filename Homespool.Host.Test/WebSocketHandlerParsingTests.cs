using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Homespool.Host.Exceptions;
using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// Covers <c>WebSocketHandler</c>'s parsing loop against the framings a Prusa printer actually
/// produces — specifically the ones that do not line up with JSON document boundaries.
/// </summary>
/// <remarks>
/// <para>
/// A WebSocket frame boundary is decided by the network, not by the sender's intent. Prusa
/// telemetry objects therefore arrive split across reads, joined together in one read, separated
/// by newlines, or any mixture of those. The handler has to reassemble them without losing
/// messages and without mistaking "not finished yet" for "malformed".
/// </para>
/// <para>
/// These tests feed the handler a real in-memory <see cref="Pipe"/>, with
/// <see cref="WriteInChunksAsync"/> dictating exactly where the boundaries fall - deliberately not
/// a substitute, because a mocking framework cannot fragment a byte stream, and the fragmentation
/// is the whole point. Parsed messages are asserted against directly via
/// <see cref="RecordingMessageDispatcher"/> rather than through stdout.
/// </para>
/// </remarks>
public class WebSocketHandlerParsingTests
{
    /// <summary>A full-shape telemetry message, matching the capture.</summary>
    private const string FullTelemetry =
        """{"job_id":301,"time_printing":4041,"time_remaining":10620,"progress":25,"temp_nozzle":214.3,"temp_bed":59.9,"target_nozzle":215.0,"target_bed":60.0,"speed":100,"flow":100,"material":"PLA","chamber":{"temp":35.6,"target_temp":20,"fan_1_rpm":0,"fan_2_rpm":0,"fan_pwm_target":-1,"led_intensity":100},"axis_z":13.00,"fan_extruder":8185,"fan_print":5038,"filament":2428351.0,"state":"PRINTING"}""";

    /// <summary>
    /// The slim shape: roughly 45% of messages in the capture carry only these five fields.
    /// </summary>
    private const string SlimTelemetry =
        """{"job_id":301,"time_printing":4042,"time_remaining":10620,"progress":25,"state":"PRINTING"}""";

    /// <summary>
    /// A <c>FILE_INFO</c>-style event whose path contains non-ASCII characters. The Connect SDK
    /// lists '¯' and '°' among the characters it forbids in file names, which is direct evidence
    /// that non-ASCII reaches these fields in practice.
    /// </summary>
    private const string EventWithNonAsciiPath =
        """{"event":"FILE_INFO","command_id":42,"state":"PRINTING","data":{"path":"/usb/målestok-90°.bgcode","display_name":"Målestok 90° — udkast"}}""";

    /// <summary>The shipped defaults - a 1 MiB message cap, which nothing here approaches.</summary>
    private static readonly IOptionsMonitor<PrusaConnectOptions> DefaultOptions =
        TestOptions.Monitor(new PrusaConnectOptions());

    /// <summary>
    /// One JSON message split across reads at 1, 2, 7, 64 and 4096 bytes arrives as one message.
    /// </summary>
    /// <remarks>
    /// A read boundary has nothing to do with a document boundary. The single-byte case is the
    /// pathological one and is the reason the handler cannot simply parse whatever a read returns.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(4096)]
    public async Task FragmentedMessageIsReassembled(int chunkSize)
    {
        // Act
        IReadOnlyList<string> received = await RunHandlerAsync(FullTelemetry, chunkSize);

        // Assert
        // A single message split across frames must still arrive exactly once, intact.
        received.Should().ContainSingle();
        received[0].Should().Contain("\"job_id\":301");
    }

    /// <summary>
    /// A message split part-way through a multi-byte UTF-8 character still reassembles intact.
    /// </summary>
    /// <remarks>
    /// Not hypothetical: the SDK forbids <c>¯</c> and <c>°</c> in filenames precisely because they
    /// reach <c>FILE_INFO.path</c>, so non-ASCII really does travel on this wire. Splitting mid
    /// character is what breaks a handler that decodes each read independently instead of buffering
    /// bytes until a document completes.
    /// </remarks>
    [Fact]
    public async Task MessageSplitMidUtf8CharacterIsReassembled()
    {
        // Arrange
        byte[] payload = Encoding.UTF8.GetBytes(EventWithNonAsciiPath);

        // '°' is two bytes in UTF-8. Splitting between them means neither half is valid UTF-8 on
        // its own, so a reader that decodes per-frame instead of per-document corrupts the text.
        int degreeSignIndex = Array.IndexOf(payload, (byte)0xC2);

        degreeSignIndex.Should().BeGreaterThan(0, "the fixture must actually contain a multi-byte character");

        // Act
        IReadOnlyList<string> received = await RunHandlerSplitOnceAsync(
            EventWithNonAsciiPath,
            splitAt: degreeSignIndex + 1);

        // Assert
        received.Should().ContainSingle();
        received[0].Should().Contain("90°", "the multi-byte character must survive reassembly");
        received[0].Should().Contain("Målestok");
    }

    /// <summary>
    /// Several objects run together with no separator are each delivered.
    /// </summary>
    /// <remarks>
    /// One of the two framings the capture actually shows, and what happens whenever the printer
    /// outruns the reader. Asserting the <i>count</i> is the point: a reader that stops after the
    /// first object passes any assertion made only inside the loop.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task ConcatenatedMessagesAreAllDelivered(int chunkSize)
    {
        // Arrange
        // No separator at all between objects — one of the two framings seen on the wire.
        string payload = FullTelemetry + SlimTelemetry + FullTelemetry;

        // Act
        IReadOnlyList<string> received = await RunHandlerAsync(payload, chunkSize);

        // Assert
        received.Should().HaveCount(3);
    }

    /// <summary>
    /// The other framing in the capture - objects separated by newlines - is handled too.
    /// </summary>
    /// <remarks>
    /// Trailing whitespace is the trap here. With default reader options
    /// <c>JsonDocument.TryParseValue</c> throws rather than returning false when the remaining buffer
    /// holds no token, and a single newline after the last object is enough to trigger it.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task NewlineDelimitedMessagesAreAllDelivered(int chunkSize)
    {
        // Arrange
        // The other framing, including a trailing newline after the final object.
        string payload = FullTelemetry + "\n" + SlimTelemetry + "\n" + FullTelemetry + "\n";

        // Act
        IReadOnlyList<string> received = await RunHandlerAsync(payload, chunkSize);

        // Assert
        received.Should().HaveCount(3);
    }

    /// <summary>
    /// A full telemetry message and a reduced one stay distinguishable after reassembly.
    /// </summary>
    /// <remarks>
    /// The firmware alternates deliberately - <c>SendTelemetry::Mode</c> is Full or Reduced, and
    /// roughly 45% of the capture is reduced. A reduced message must not be mistaken for a full one
    /// reporting nulls, or the merge in phase 3 would overwrite good values with absent ones.
    /// </remarks>
    [Fact]
    public async Task BothTelemetryShapesSurviveAndRemainDistinguishable()
    {
        // Act
        IReadOnlyList<string> received = await RunHandlerAsync(FullTelemetry + "\n" + SlimTelemetry, chunkSize: 3);

        // Assert
        received.Should().HaveCount(2);

        // The merge logic in phase 3 depends on being able to tell these apart: the slim message
        // must not be mistaken for a full one reporting nulls.
        received[0].Should().Contain("temp_nozzle");
        received[1].Should().NotContain("temp_nozzle");
    }

    /// <summary>
    /// A document that never finishes is disconnected once it passes the configured limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gap this closes: an incomplete document is buffered, correctly, because real messages
    /// arrive across many frames - the largest measured took 184. But nothing declared one absurd,
    /// and <c>PipeReader.Create</c> over a stream is a <c>StreamPipeReader</c>, which has no
    /// <c>PauseWriterThreshold</c> to fall back on. A value opened and never closed grew the buffer
    /// until the process died, taking every other printer with it.
    /// </para>
    /// <para>
    /// A small cap is used here rather than the shipped 1 MiB so the test stays fast; the number is
    /// the configuration's business, and <c>PrusaConnectOptions</c> carries the reasoning for it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADocumentThatNeverEndsIsCutOffAtTheLimit()
    {
        // Arrange
        Pipe wire = new();

        WebSocketHandler handler = new(NullLogger<WebSocketHandler>.Instance,
                                       new RecordingMessageDispatcher(),
                                       TestOptions.Monitor(new PrusaConnectOptions { MaxIncomingMessageBytes = 4096 }));

        // Act
        Task run = handler.HandlePrusaWebsocket(wire.Reader, printerId: 7,
                                                Substitute.For<IPrinterConnectionActor>(), CancellationToken.None);

        // Valid JSON as far as it goes, and never closed - the shape that used to buffer for ever.
        await WriteInChunksAsync(wire.Writer,
                                 Encoding.UTF8.GetBytes("{\"a\":\"" + new string('x', 8192)), chunkSize: 1024);

        // Assert
        PrinterMessageTooLargeException thrown = await Assert.ThrowsAsync<PrinterMessageTooLargeException>(() => run);

        thrown.PrinterId.Should().Be(7);
        thrown.LimitBytes.Should().Be(4096);
        thrown.BufferedBytes.Should().BeGreaterThan(4096, "the limit is what was passed, not what was aimed at");
    }

    /// <summary>
    /// A message that is merely large still gets through - the cap must not fire on real traffic.
    /// </summary>
    /// <remarks>
    /// Sized just under the limit and split across frames, because the failure this guards against is
    /// a cap that counts the wrong thing: bytes held <em>while waiting</em>, reset every time a
    /// document completes, not a running total across the connection. Counting cumulatively would
    /// disconnect a healthy printer after enough ordinary telemetry, which is far worse than the
    /// problem being solved.
    /// </remarks>
    [Fact]
    public async Task ALargeButCompleteMessageIsNotCutOff()
    {
        // Arrange
        Pipe wire = new();
        RecordingMessageDispatcher dispatcher = new();

        WebSocketHandler handler = new(NullLogger<WebSocketHandler>.Instance, dispatcher,
                                       TestOptions.Monitor(new PrusaConnectOptions { MaxIncomingMessageBytes = 4096 }));

        // Act
        Task run = handler.HandlePrusaWebsocket(wire.Reader, printerId: 7,
                                                Substitute.For<IPrinterConnectionActor>(), CancellationToken.None);

        // Three complete messages, each close to the cap: over 4096 bytes in total, under it each.
        for (int i = 0; i < 3; i++)
        {
            await WriteInChunksAsync(wire.Writer,
                                     Encoding.UTF8.GetBytes($$"""{"job_id":{{i}},"pad":"{{new string('x', 3000)}}"}""" + "\n"),
                                     chunkSize: 512);
        }

        await wire.Writer.CompleteAsync();
        await run;

        // Assert
        dispatcher.Received.Should().HaveCount(3, "the counter resets when a document completes");
    }

    /// <summary>
    /// Genuinely broken input throws <see cref="JsonException"/> out of the handler - the signal
    /// the controller closes the socket (with <c>PolicyViolation</c>) on.
    /// </summary>
    /// <remarks>
    /// Guards the opposite direction from the fragmentation tests. Tolerating a partial document
    /// because more bytes may arrive must not become tolerating garbage forever - a printer sending
    /// nonsense should be disconnected, not waited on.
    /// </remarks>
    [Fact]
    public async Task MalformedJsonThrowsForTheCallerToCloseOn()
    {
        // Arrange
        // Guards the other direction: the fragmentation handling must not swallow genuinely
        // broken input. A printer sending garbage should still be disconnected.
        Pipe wire = new();

        WebSocketHandler handler = new(NullLogger<WebSocketHandler>.Instance, new RecordingMessageDispatcher(), DefaultOptions);

        // Act
        Task run = handler.HandlePrusaWebsocket(wire.Reader, printerId: 1, Substitute.For<IPrinterConnectionActor>(),
                                                CancellationToken.None);

        await WriteInChunksAsync(wire.Writer, Encoding.UTF8.GetBytes("""{"job_id":301,,,}"""), chunkSize: 4096);
        await wire.Writer.CompleteAsync();

        Func<Task> act = async () => await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<JsonException>();
    }

    /// <summary>
    /// A brace inside a string or a comment does not end a message, however the bytes are split.
    /// </summary>
    /// <remarks>
    /// The handler finds where a document ends before parsing it, so it is exactly the places a
    /// brace is not structure that it has to get right: strings, a quote or backslash escaped inside
    /// one, and both kinds of comment the reader is configured to skip - including a line comment
    /// ended by a bare carriage return, and a block comment closed by <c>**/</c>. Ending early would
    /// hand the parser half a message; ending late would swallow the next one, so the count is
    /// asserted as well as the content.
    /// </remarks>
    /// <param name="chunkSize">Bytes per read.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task BracesInsideStringsAndCommentsDoNotEndAMessage(int chunkSize)
    {
        // Arrange
        // Each comment hides a brace, and the message's own closing brace follows the \r-ended one:
        // a scanner that missed any of these would end early or run on into the next message.
        const string Tricky =
            """/* {{ */ {"path":"/usb/{a}[b]\"}\\","n":1 /** } **/ ,"l":[[1],{"x":[]}],"m":{"k":"]" // ]""" + "\n" +
            "} // }" + "\r" + "}";

        // Act
        IReadOnlyList<string> received = await RunHandlerAsync(Tricky + SlimTelemetry, chunkSize);

        // Assert
        received.Should().HaveCount(2, "a brace in a string or comment must neither end a message nor hide the next");

        // The raw text keeps the comments inside the object, so reading it back has to allow them.
        using JsonDocument first = JsonDocument.Parse(received[0],
                                                      new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

        first.RootElement.GetProperty("path").GetString().Should().Be("/usb/{a}[b]\"}\\");
        first.RootElement.GetProperty("m").GetProperty("k").GetString().Should().Be("]");
        received[1].Should().Be(SlimTelemetry);
    }

    /// <summary>
    /// A document that is not an object, or a <c>/</c> that starts no comment, is a protocol
    /// violation found before the document ends.
    /// </summary>
    /// <remarks>
    /// Every message either reference client renders is an object. A top-level number or literal
    /// also has no closing byte to look for, so the handler could only find its end by re-parsing
    /// the whole buffer on every read - the cost the scan exists to remove.
    /// </remarks>
    /// <param name="payload">What the printer sends.</param>
    [Theory]
    [InlineData("[1]")]
    [InlineData("\"PRINTING\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("/* a comment first */ [1]")]
    [InlineData("""/x {"state":"IDLE"}""")]
    [InlineData("""{"state":"IDLE" / }""")]
    public async Task NotAnObjectOrAStraySlashThrowsForTheCallerToCloseOn(string payload)
    {
        // Act
        Func<Task> act = () => RunHandlerAsync(payload, chunkSize: 1);

        // Assert
        await act.Should().ThrowAsync<JsonException>();
    }

    /// <summary>
    /// The printer closing part-way through a message is malformed input, not an ordinary end.
    /// </summary>
    /// <remarks>
    /// An unfinished document is buffered while more may arrive. Once the stream has ended nothing
    /// will, and returning quietly would drop the half message without a word.
    /// </remarks>
    [Fact]
    public async Task ClosingPartWayThroughAMessageThrows()
    {
        // Act
        Func<Task> act = () => RunHandlerAsync(SlimTelemetry + """{"state":"IDL""", chunkSize: 4096);

        // Assert
        await act.Should().ThrowAsync<JsonException>();
    }

    /// <summary>
    /// A comment after the last message is not an unfinished message: the printer closing then is an
    /// ordinary end, and the message before it is still delivered.
    /// </summary>
    /// <remarks>
    /// A line comment the close cuts off counts as finished, since nothing but the end of the stream
    /// was left to end it.
    /// </remarks>
    /// <param name="trailer">What follows the last message.</param>
    [Theory]
    [InlineData("// tail")]
    [InlineData("// tail\n")]
    [InlineData("/* tail */")]
    [InlineData(" /* a */ // b")]
    public async Task ACommentAfterTheLastMessageEndsQuietly(string trailer)
    {
        // Act
        IReadOnlyList<string> received = await RunHandlerAsync(SlimTelemetry + trailer, chunkSize: 1);

        // Assert
        received.Should().ContainSingle().Which.Should().Be(SlimTelemetry);
    }

    /// <summary>
    /// A comment the close leaves unfinished still throws when it cannot be the end: a block comment
    /// missing its <c>*/</c>, or a line comment inside a message that never closed.
    /// </summary>
    /// <param name="trailer">What follows the last complete message.</param>
    [Theory]
    [InlineData("/* tail")]
    [InlineData("""{"state":"IDLE" // open""")]
    public async Task AnUnfinishedCommentAtTheCloseThrows(string trailer)
    {
        // Act
        Func<Task> act = () => RunHandlerAsync(SlimTelemetry + trailer, chunkSize: 1);

        // Assert
        await act.Should().ThrowAsync<JsonException>();
    }

    /// <summary>
    /// A message carrying a non-finite number is delivered with its quoted literal in its place, the message
    /// after it is delivered too, and the dispatcher is told what was replaced - however the bytes
    /// are split, a split inside the token included.
    /// </summary>
    /// <remarks>
    /// Firmware renders a gcode file's float metadata with no finiteness check, so a valid file
    /// saying <c>; layer_height = nan</c> produces exactly this <c>FILE_INFO</c>, and it used to cost
    /// the printer its connection. The second message is what proves the arithmetic: the mended copy
    /// is longer than what arrived, and consuming the copy's length from the wire instead of the
    /// original's would eat the front of the next message.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task ANonFiniteNumberIsDeliveredAsItsLiteralAndTheNextMessageSurvives(int chunkSize)
    {
        // Arrange
        const string FirmwareFileInfo =
            """{"event":"FILE_INFO","command_id":42,"state":"IDLE","data":{"size":1024,"layer_height":nan,"max_layer_z":-inf,"path":"/usb/NAN.GCO"}}""";

        Pipe wire = new();

        RecordingMessageDispatcher dispatcher = new();
        WebSocketHandler handler = new(NullLogger<WebSocketHandler>.Instance, dispatcher, DefaultOptions);

        // Act
        Task run = handler.HandlePrusaWebsocket(wire.Reader, printerId: 1, Substitute.For<IPrinterConnectionActor>(),
                                                CancellationToken.None);

        await WriteInChunksAsync(wire.Writer, Encoding.UTF8.GetBytes(FirmwareFileInfo + "\n" + SlimTelemetry), chunkSize);
        await wire.Writer.CompleteAsync();

        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        dispatcher.Received.Should().Equal(
            """{"event":"FILE_INFO","command_id":42,"state":"IDLE","data":{"size":1024,"layer_height":"NaN","max_layer_z":"-Infinity","path":"/usb/NAN.GCO"}}""",
            SlimTelemetry);

        dispatcher.NonFinite.Should().HaveCount(2);
        dispatcher.NonFinite[0].Should().Equal(new NonFiniteToken(2, "nan"), new NonFiniteToken(3, "-inf"));
        dispatcher.NonFinite[1].Should().BeEmpty();
    }

    /// <summary>
    /// Mending non-finite numbers must not have made the parser forgiving of anything else: a near
    /// miss, and a real one beside other damage, still end the connection.
    /// </summary>
    [Theory]
    [InlineData("""{"state":"IDLE","v":nanx}""")]
    [InlineData("""{"state":"IDLE","v":infinit}""")]
    [InlineData("""{"state":"IDLE","v":nan,,"w":1}""")]
    [InlineData("""{"state":"IDLE" nan}""")]
    public async Task AlmostANonFiniteNumberStillThrowsForTheCallerToCloseOn(string payload)
    {
        // Act
        Func<Task> act = () => RunHandlerAsync(payload, chunkSize: 1);

        // Assert
        await act.Should().ThrowAsync<JsonException>();
    }

    private static Task<IReadOnlyList<string>> RunHandlerAsync(string payload, int chunkSize)
    {
        return RunHandlerAsync(Encoding.UTF8.GetBytes(payload), [chunkSize]);
    }

    /// <summary>Delivers the payload as exactly two frames, split at <paramref name="splitAt"/>.</summary>
    private static Task<IReadOnlyList<string>> RunHandlerSplitOnceAsync(string payload, int splitAt)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload);

        return RunHandlerAsync(bytes, [splitAt, bytes.Length - splitAt]);
    }

    private static async Task<IReadOnlyList<string>> RunHandlerAsync(byte[] payload, int[] chunkSizes)
    {
        Pipe wire = new();

        RecordingMessageDispatcher dispatcher = new();
        WebSocketHandler handler = new(NullLogger<WebSocketHandler>.Instance, dispatcher, DefaultOptions);

        Task run = handler.HandlePrusaWebsocket(wire.Reader, printerId: 1, Substitute.For<IPrinterConnectionActor>(),
                                                CancellationToken.None);

        if (chunkSizes.Length == 1)
        {
            await WriteInChunksAsync(wire.Writer, payload, chunkSizes[0]);
        }
        else
        {
            int offset = 0;

            foreach (int size in chunkSizes)
            {
                await WriteInChunksAsync(wire.Writer, payload[offset..(offset + size)], size);
                offset += size;
            }
        }

        // Completing the writer is the peer closing the connection: the handler drains what is
        // buffered and returns at the completed read.
        await wire.Writer.CompleteAsync();

        // A generous ceiling: this exists so a regression that spins or blocks fails the test
        // instead of hanging the suite, which is how the old parsing spike used to behave.
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        return dispatcher.Received;
    }

    /// <summary>
    /// Writes <paramref name="payload"/> in <paramref name="chunkSize"/>-byte pieces, flushing each
    /// one so it becomes a separate read - the equivalent of arriving in separate frames, with the
    /// boundary falling wherever the test says, not where a document ends.
    /// </summary>
    private static async Task WriteInChunksAsync(PipeWriter writer, byte[] payload, int chunkSize)
    {
        for (int offset = 0; offset < payload.Length; offset += chunkSize)
        {
            int length = Math.Min(chunkSize, payload.Length - offset);

            // PipeWriter.WriteAsync copies and flushes in one step, so each chunk becomes visible
            // to the reader on its own.
            await writer.WriteAsync(payload.AsMemory(offset, length));
        }
    }

    /// <summary>
    /// Captures each classified message's raw text instead of producing a typed message.
    /// Subclasses <see cref="MessageDispatcher"/> rather than substituting it: <c>Classify</c> is
    /// overridden wholesale to capture raw text, so these tests assert on reassembly without
    /// depending on deserialization behaviour (covered by <c>MessageDispatcherTests</c>). Returning
    /// null makes the handler post nothing, so the actor above can stay a bare substitute.
    /// </summary>
    private sealed class RecordingMessageDispatcher()
        : MessageDispatcher(NullLogger<MessageDispatcher>.Instance,
                            new UnknownFieldTracker(NullLogger<UnknownFieldTracker>.Instance),
                            TimeProvider.System,
                            PrinterTrafficLogTests.Off)
    {
        public List<string> Received { get; } = [];

        public List<IReadOnlyList<NonFiniteToken>> NonFinite { get; } = [];

        public override ConnectionMessage? Classify(int printerId, JsonElement root, IReadOnlyList<NonFiniteToken> nonFinite)
        {
            Received.Add(root.GetRawText());
            NonFinite.Add(nonFinite);

            return null;
        }
    }
}
