using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.FakePrinter.Cli;

/// <summary>
/// Thin driver over <see cref="FakePrinterClient"/> against a genuinely running server - the mode
/// that reaches Kestrel, real TCP and SIGTERM, which the in-process tests cannot
/// (<c>notes/fake-printer-harness.md</c>, "Form factor"). Three verbs:
/// <c>enrol</c> (register, print the claim code, poll for the token, save the identity file),
/// <c>run</c> (connect and behave like a printer until Ctrl-C), and
/// <c>blast</c> (flood telemetry with no delays, for load/backpressure work).
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>Entry point. Returns 0 on success, 1 on bad usage or failure.</summary>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();

            return 1;
        }

        Dictionary<string, string> named = ParseNamedArguments(args);

        // Refused rather than ignored. An unrecognised option is silently dropped by the parser above,
        // so leaving this one to rot would mean a run that asked for events quietly sent none - the
        // worst outcome for a load rig, whose whole output is numbers nobody can sanity-check by eye.
        if (named.ContainsKey("events-every"))
        {
            Console.WriteLine("--events-every is gone: its unit read as time and was a count.");
            Console.WriteLine("Use --events-every-nth <n> for one event per n messages (fixed ratio,");
            Console.WriteLine("what the buffer-ceiling rigs want), or --events-every-seconds <n> for");
            Console.WriteLine("one event per n seconds (wall clock, what a blast wants).");

            return 1;
        }

        using CancellationTokenSource cancellation = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            switch (args[0])
            {
                case "enrol":
                    return await EnrolAsync(named, cancellation.Token);

                case "run":
                    return await RunAsync(named, blast: false, cancellation.Token);

                case "blast":
                    return await RunAsync(named, blast: true, cancellation.Token);

                default:
                    PrintUsage();

                    return 1;
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Stopped.");

            return 0;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  fakeprinter enrol  --server <url> [--identity <file>]");
        Console.WriteLine(
            "  fakeprinter run    --server <url> [--identity <file>] [--capture <path>] [--printing] [--interval-ms <n>] [--events-every-nth <n>] [--events-every-seconds <n>] [--tools <n>] [--mmu] [--junk-fields <n>] [--junk-distinct]");
        Console.WriteLine(
            "  fakeprinter blast  --server <url> [--identity <file>] [--events-every-nth <n>] [--events-every-seconds <n>] [--tools <n>] [--mmu] [--junk-fields <n>] [--junk-distinct]");
        Console.WriteLine();
        Console.WriteLine("--events-every-nth <n> makes every n-th message a STATE_CHANGED event rather");
        Console.WriteLine("than telemetry - a fixed ratio, 10 matching the firmware ratio, which is what");
        Console.WriteLine("the buffer-ceiling rigs turn on. --events-every-seconds <n> pins events to the");
        Console.WriteLine("clock instead, which is what a blast wants: a ratio at blast speed would mean");
        Console.WriteLine("tens of thousands of events a second.");
        Console.WriteLine();
        Console.WriteLine("--no-websocket uses the pre-websocket HTTP transport - one POST to /p/telemetry or");
        Console.WriteLine("/p/events per message - which is what firmware built with WEBSOCKET off speaks, a");
        Console.WriteLine("6.2.6 MK3.5 among them. No commands arrive on that transport, so the answer");
        Console.WriteLine("policies do not run.");
        Console.WriteLine();
        Console.WriteLine("--tools <n> reports n tools, which emits the per-slot \"slot\" object firmware only");
        Console.WriteLine("sends above one tool - one extra persisted row per tool per sample. --mmu adds the");
        Console.WriteLine("MMU-only state/command pair.");
        Console.WriteLine();
        Console.WriteLine("--junk-fields <n> adds n properties the server does not model, for exercising the");
        Console.WriteLine("unknown-field tracker; --junk-distinct makes every name unique, which is what");
        Console.WriteLine("drives its distinct-name cap. No printer sends either shape.");
        Console.WriteLine();
        Console.WriteLine("The identity file (default fakeprinter.json) holds the fingerprint and, after");
        Console.WriteLine("enrol, the token. It is a credential - keep it out of the repository.");
    }

    private static Dictionary<string, string> ParseNamedArguments(string[] args)
    {
        Dictionary<string, string> named = new(StringComparer.Ordinal);

        for (int i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string key = args[i][2..];
            bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            named[key] = hasValue ? args[++i] : "true";
        }

        return named;
    }

    private static Uri RequireServer(Dictionary<string, string> named)
    {
        if (!named.TryGetValue("server", out string? server))
        {
            throw new ArgumentException("--server <url> is required.");
        }

        return new Uri(server, UriKind.Absolute);
    }

    private static string IdentityPath(Dictionary<string, string> named)
    {
        return named.TryGetValue("identity", out string? path) ? path : "fakeprinter.json";
    }

    private static async Task<int> EnrolAsync(Dictionary<string, string> named, CancellationToken cancellationToken)
    {
        Uri server = RequireServer(named);
        string path = IdentityPath(named);

        PrinterIdentity identity = PrinterIdentity.CreateRandom();
        FakePrinterOptions options = new() { BaseAddress = server };
        TimeProvider timeProvider = TimeProvider.System;
        await using FakePrinterClient client = new(identity, timeProvider, options);

        using HttpClient http = new() { BaseAddress = server };

        string code = await client.RegisterAsync(http, cancellationToken);
        Console.WriteLine($"Registered. Fingerprint: {identity.Fingerprint}");
        Console.WriteLine($"Claim code: {code}");
        Console.WriteLine("Claim it in the web UI (Printers -> Claim); polling every 5s like the firmware does...");

        // The firmware polls every 5 seconds, forever (registrator.cpp; the SDK gives up after
        // 30 minutes - Ctrl-C plays that role here).
        string token = await client.EnrolAsync(http, code, TimeSpan.FromSeconds(5), cancellationToken);

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(StoredIdentity.From(identity, token), SerializerOptions),
            cancellationToken);

        Console.WriteLine($"Enrolled. Identity + token written to {path}.");

        return 0;
    }

    private static async Task<int> RunAsync(Dictionary<string, string> named, bool blast, CancellationToken cancellationToken)
    {
        Uri server = RequireServer(named);
        string path = IdentityPath(named);

        if (!File.Exists(path))
        {
            Console.WriteLine($"No identity file at {path} - run enrol first.");

            return 1;
        }

        StoredIdentity? stored = JsonSerializer.Deserialize<StoredIdentity>(await File.ReadAllTextAsync(path, cancellationToken));

        if (stored?.Token is null)
        {
            Console.WriteLine($"{path} holds no token - run enrol first.");

            return 1;
        }

        ITelemetrySource source = BuildSource(named, blast);
        FakePrinterOptions options = new()
        {
            BaseAddress = server,
            TelemetrySource = source,
        };

        TimeProvider timeProvider = TimeProvider.System;
        await using FakePrinterClient client = new(stored.ToIdentity(), timeProvider, options);
        client.Token = stored.Token;

        if (named.ContainsKey("printing"))
        {
            client.Device.StartPrint(jobId: 1);
        }

        if (named.ContainsKey("no-websocket"))
        {
            // The pre-websocket transport: no connection to make, so there is nothing to announce
            // until the first POST has been accepted. A 401 or a 404 throws out of RunHttpAsync
            // rather than being retried, which is what makes a misconfigured run visible.
            using HttpClient httpClient = new() { BaseAddress = server };

            Console.WriteLine($"Posting as {stored.ToIdentity().HeaderFingerprint}... Ctrl-C to stop.");

            await client.RunHttpAsync(httpClient, cancellationToken);
            Console.WriteLine("Telemetry source ended.");

            return 0;
        }

        await client.ConnectAsync(cancellationToken: cancellationToken);
        Console.WriteLine($"Connected as {stored.ToIdentity().HeaderFingerprint}... Ctrl-C to stop.");

        await client.RunAsync(cancellationToken);
        Console.WriteLine("Connection ended.");

        return 0;
    }

    private static ITelemetrySource BuildSource(Dictionary<string, string> named, bool blast)
    {
        return AddUnknownFields(named, MixEvents(named, BuildTelemetrySource(named, blast)));
    }

    /// <summary>
    /// Wraps the source so each message carries unmodelled properties, when <c>--junk-fields</c> asks
    /// for it. Off unless asked; nothing a real printer sends looks like this.
    /// </summary>
    private static ITelemetrySource AddUnknownFields(Dictionary<string, string> named, ITelemetrySource source)
    {
        if (!named.TryGetValue("junk-fields", out string? value) || !int.TryParse(value, out int fields) || fields < 1)
        {
            return source;
        }

        return new UnknownFieldTelemetrySource(source)
        {
            FieldsPerMessage = fields,
            Distinct = named.ContainsKey("junk-distinct"),
        };
    }

    /// <summary>
    /// Wraps the telemetry source so some messages are events instead, by count or by clock. Off
    /// unless asked: a run that requests neither sends exactly what it did before these existed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two flags, because the unit is not a detail.</b> A count keeps a fixed ratio between the
    /// two streams, which is what the writer's buffer ceilings turn on - the claim that events outlive
    /// samples rests on the ratio, so a rig measuring that wants <c>--events-every-nth</c>. A ratio
    /// scales with the send rate, though, so at blast speed one-in-ten becomes tens of thousands of
    /// events a second, which no printer resembles; a burst wants <c>--events-every-seconds</c>.
    /// </para>
    /// <para>
    /// They were briefly one flag named <c>--events-every</c>, whose unit read as time and was a
    /// count. It misled the person who asked for it, on the day after it was added, so it is gone
    /// rather than merely documented.
    /// </para>
    /// </remarks>
    private static ITelemetrySource MixEvents(Dictionary<string, string> named, ITelemetrySource source)
    {
        if (named.TryGetValue("events-every-nth", out string? nth) && int.TryParse(nth, out int every) && every >= 1)
        {
            source = new EventMixingTelemetrySource(source) { EventEvery = every };
        }

        if (named.TryGetValue("events-every-seconds", out string? seconds)
            && double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out double interval)
            && interval > 0)
        {
            source = new TimedEventTelemetrySource(source, TimeProvider.System)
            {
                EventInterval = TimeSpan.FromSeconds(interval),
            };
        }

        return source;
    }

    private static ITelemetrySource BuildTelemetrySource(Dictionary<string, string> named, bool blast)
    {
        if (named.TryGetValue("capture", out string? capturePath))
        {
            TimeSpan delay = blast ? TimeSpan.Zero : IntervalFrom(named, defaultMilliseconds: 1000);

            return new CaptureReplaySource(capturePath, delay);
        }

        if (blast)
        {
            return new SyntheticTelemetrySource
            {
                IdleInterval = TimeSpan.Zero,
                PrintingInterval = TimeSpan.Zero,
                Readings = ReadingsFrom(named),
            };
        }

        if (named.ContainsKey("interval-ms"))
        {
            TimeSpan interval = IntervalFrom(named, defaultMilliseconds: 1000);

            return new SyntheticTelemetrySource
            {
                IdleInterval = interval,
                PrintingInterval = interval,
                Readings = ReadingsFrom(named),
            };
        }

        return new SyntheticTelemetrySource { Readings = ReadingsFrom(named) };
    }

    /// <summary>
    /// The analog readings, with <c>--tools</c> deciding whether a <c>slot</c> object is emitted at
    /// all. Firmware sends one only above one tool, so the default reproduces the capture printer.
    /// </summary>
    /// <remarks>
    /// <c>--mmu</c> adds the MMU-only <c>state</c>/<c>command</c> pair. Absent is meaningful and not
    /// the same as zero, so it stays null unless asked for - see backlog.md on <c>mmu.enabled</c>.
    /// </remarks>
    private static TelemetryReadings ReadingsFrom(Dictionary<string, string> named)
    {
        int tools = named.TryGetValue("tools", out string? value) && int.TryParse(value, out int parsed) ? Math.Max(parsed, 1) : 1;

        bool mmu = named.ContainsKey("mmu");

        return new TelemetryReadings
        {
            Tools = tools,
            ActiveTool = 1,
            MmuState = mmu ? 3 : null,
            MmuCommand = mmu ? "C" : null,
        };
    }

    private static TimeSpan IntervalFrom(Dictionary<string, string> named, int defaultMilliseconds)
    {
        return named.TryGetValue("interval-ms", out string? value) && int.TryParse(value, out int milliseconds) ?
            TimeSpan.FromMilliseconds(milliseconds) :
            TimeSpan.FromMilliseconds(defaultMilliseconds);
    }
}
