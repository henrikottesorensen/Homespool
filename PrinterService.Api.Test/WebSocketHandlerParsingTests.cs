using System.Net.WebSockets;
using System.Text;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using PrinterService.Api.PrusaConnect;
using PrinterService.Data;

namespace PrinterService.Api.Test;

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
/// These tests drive the handler through <see cref="FakeWebSocketPipe"/>, which lets each case
/// dictate exactly where the boundaries fall.
/// </para>
/// <para>
/// Parallelism is disabled for this class because the handler currently reports parsed messages
/// via <see cref="Console"/>, and capturing that means swapping global state. That capture is a
/// temporary seam: once the dispatcher lands in phase 2 it should be injected, and these tests
/// should assert against it directly rather than through stdout.
/// </para>
/// </remarks>
[Collection(nameof(ConsoleCapturingCollection))]
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(4096)]
    public async Task FragmentedMessageIsReassembled(int chunkSize)
    {
        IReadOnlyList<string> received = await RunHandlerAsync(FullTelemetry, chunkSize);

        // A single message split across frames must still arrive exactly once, intact.
        received.Should().ContainSingle();
        received[0].Should().Contain("\"job_id\":301");
    }

    [Fact]
    public async Task MessageSplitMidUtf8CharacterIsReassembled()
    {
        byte[] payload = Encoding.UTF8.GetBytes(EventWithNonAsciiPath);

        // '°' is two bytes in UTF-8. Splitting between them means neither half is valid UTF-8 on
        // its own, so a reader that decodes per-frame instead of per-document corrupts the text.
        int degreeSignIndex = Array.IndexOf(payload, (byte)0xC2);

        degreeSignIndex.Should().BeGreaterThan(0, "the fixture must actually contain a multi-byte character");

        IReadOnlyList<string> received = await RunHandlerSplitOnceAsync(
            EventWithNonAsciiPath,
            splitAt: degreeSignIndex + 1);

        received.Should().ContainSingle();
        received[0].Should().Contain("90°", "the multi-byte character must survive reassembly");
        received[0].Should().Contain("Målestok");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task ConcatenatedMessagesAreAllDelivered(int chunkSize)
    {
        // No separator at all between objects — one of the two framings seen on the wire.
        string payload = FullTelemetry + SlimTelemetry + FullTelemetry;

        IReadOnlyList<string> received = await RunHandlerAsync(payload, chunkSize);

        received.Should().HaveCount(3);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task NewlineDelimitedMessagesAreAllDelivered(int chunkSize)
    {
        // The other framing, including a trailing newline after the final object.
        string payload = FullTelemetry + "\n" + SlimTelemetry + "\n" + FullTelemetry + "\n";

        IReadOnlyList<string> received = await RunHandlerAsync(payload, chunkSize);

        received.Should().HaveCount(3);
    }

    [Fact]
    public async Task BothTelemetryShapesSurviveAndRemainDistinguishable()
    {
        IReadOnlyList<string> received = await RunHandlerAsync(FullTelemetry + "\n" + SlimTelemetry, chunkSize: 3);

        received.Should().HaveCount(2);

        // The merge logic in phase 3 depends on being able to tell these apart: the slim message
        // must not be mistaken for a full one reporting nulls.
        received[0].Should().Contain("temp_nozzle");
        received[1].Should().NotContain("temp_nozzle");
    }

    [Fact]
    public async Task MalformedJsonClosesTheConnection()
    {
        // Guards the other direction: the fragmentation handling must not swallow genuinely
        // broken input. A printer sending garbage should still be disconnected.
        using FakeWebSocketPipe pipe = new();
        using PSDbContext context = CreateContext();

        WebSocketHandler handler = new(context, NullLogger<WebSocketHandler>.Instance);

        Task run = handler.HandlePrusaWebsocket(pipe, CancellationToken.None);

        await pipe.WriteInChunksAsync(Encoding.UTF8.GetBytes("""{"job_id":301,,,}"""), chunkSize: 4096);
        await pipe.FinishAsync();

        Func<Task> act = async () => await run.WaitAsync(TimeSpan.FromSeconds(10));

        await act.Should().ThrowAsync<System.Text.Json.JsonException>();

        pipe.CompleteAsyncCalled.Should().BeTrue();
        pipe.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
    }

    /// <summary>
    /// The handler does not touch the context yet, but it is a constructor dependency.
    /// </summary>
    /// <remarks>
    /// Returns the context rather than constructing the handler, so the caller owns its lifetime and
    /// can scope it with <c>using</c>. Creating it inside and returning only the handler left it
    /// undisposed on every test.
    /// </remarks>
    private static PSDbContext CreateContext()
    {
        DbContextOptions<PSDbContext> options = new DbContextOptionsBuilder<PSDbContext>()
                                                .UseSqlite("Filename=:memory:")
                                                .Options;

        return new PSDbContext(options);
    }

    private static Task<IReadOnlyList<string>> RunHandlerAsync(string payload, int chunkSize) =>
        RunHandlerAsync(Encoding.UTF8.GetBytes(payload), [chunkSize]);

    /// <summary>Delivers the payload as exactly two frames, split at <paramref name="splitAt"/>.</summary>
    private static Task<IReadOnlyList<string>> RunHandlerSplitOnceAsync(string payload, int splitAt)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload);

        return RunHandlerAsync(bytes, [splitAt, bytes.Length - splitAt]);
    }

    private static async Task<IReadOnlyList<string>> RunHandlerAsync(byte[] payload, int[] chunkSizes)
    {
        using FakeWebSocketPipe pipe = new();
        using PSDbContext context = CreateContext();

        WebSocketHandler handler = new(context, NullLogger<WebSocketHandler>.Instance);

        TextWriter originalOut = Console.Out;
        using StringWriter captured = new();

        try
        {
            Console.SetOut(captured);

            Task run = handler.HandlePrusaWebsocket(pipe, CancellationToken.None);

            if (chunkSizes.Length == 1)
            {
                await pipe.WriteInChunksAsync(payload, chunkSizes[0]);
            }
            else
            {
                int offset = 0;

                foreach (int size in chunkSizes)
                {
                    await pipe.WriteInChunksAsync(payload[offset..(offset + size)], size);
                    offset += size;
                }
            }

            await pipe.FinishAsync();

            // A generous ceiling: this exists so a regression that spins or blocks fails the test
            // instead of hanging the suite, which is how the old parsing spike used to behave.
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return captured.ToString()
                       .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }
}

[CollectionDefinition(nameof(ConsoleCapturingCollection), DisableParallelization = true)]
public class ConsoleCapturingCollection;
