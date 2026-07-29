using System;
using System.Collections.Generic;
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
/// <c>enroll</c> (register, print the claim code, poll for the token, save the identity file),
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
                case "enroll":
                    return await EnrollAsync(named, cancellation.Token);

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
        Console.WriteLine("  fakeprinter enroll --server <url> [--identity <file>]");
        Console.WriteLine("  fakeprinter run    --server <url> [--identity <file>] [--capture <path>] [--printing] [--interval-ms <n>] [--events-every <n>]");
        Console.WriteLine("  fakeprinter blast  --server <url> [--identity <file>] [--events-every <n>]");
        Console.WriteLine();
        Console.WriteLine("--events-every <n> makes every n-th message a STATE_CHANGED event rather than");
        Console.WriteLine("telemetry, for exercising the event path under load. 10 matches the firmware ratio.");
        Console.WriteLine();
        Console.WriteLine("The identity file (default fakeprinter.json) holds the fingerprint and, after");
        Console.WriteLine("enroll, the token. It is a credential - keep it out of the repository.");
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

    private static async Task<int> EnrollAsync(Dictionary<string, string> named, CancellationToken cancellationToken)
    {
        Uri server = RequireServer(named);
        string path = IdentityPath(named);

        PrinterIdentity identity = PrinterIdentity.CreateRandom();
        FakePrinterOptions options = new() { BaseAddress = server };
        await using FakePrinterClient client = new(identity, options);

        using HttpClient http = new() { BaseAddress = server };

        string code = await client.RegisterAsync(http, cancellationToken);
        Console.WriteLine($"Registered. Fingerprint: {identity.Fingerprint}");
        Console.WriteLine($"Claim code: {code}");
        Console.WriteLine("Claim it in the web UI (Printers -> Claim); polling every 5s like the firmware does...");

        // The firmware polls every 5 seconds, forever (registrator.cpp; the SDK gives up after
        // 30 minutes - Ctrl-C plays that role here).
        string token = await client.EnrollAsync(http, code, TimeSpan.FromSeconds(5), cancellationToken);

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
            Console.WriteLine($"No identity file at {path} - run enroll first.");

            return 1;
        }

        StoredIdentity? stored = JsonSerializer.Deserialize<StoredIdentity>(await File.ReadAllTextAsync(path, cancellationToken));

        if (stored?.Token is null)
        {
            Console.WriteLine($"{path} holds no token - run enroll first.");

            return 1;
        }

        ITelemetrySource source = BuildSource(named, blast);
        FakePrinterOptions options = new()
        {
            BaseAddress = server,
            TelemetrySource = source,
        };

        await using FakePrinterClient client = new(stored.ToIdentity(), options);
        client.Token = stored.Token;

        if (named.ContainsKey("printing"))
        {
            client.Device.StartPrint(jobId: 1);
        }

        await client.ConnectAsync(cancellationToken: cancellationToken);
        Console.WriteLine($"Connected as {stored.ToIdentity().HeaderFingerprint}... Ctrl-C to stop.");

        await client.RunAsync(cancellationToken);
        Console.WriteLine("Connection ended.");

        return 0;
    }

    private static ITelemetrySource BuildSource(Dictionary<string, string> named, bool blast)
    {
        return MixEvents(named, BuildTelemetrySource(named, blast));
    }

    /// <summary>
    /// Wraps the telemetry source so every N-th message is an event, when <c>--events-every</c> asks
    /// for it. Off unless asked: a run that does not request events should send exactly what it did
    /// before this option existed.
    /// </summary>
    private static ITelemetrySource MixEvents(Dictionary<string, string> named, ITelemetrySource source)
    {
        if (!named.TryGetValue("events-every", out string? value) || !int.TryParse(value, out int every) || every < 1)
        {
            return source;
        }

        return new EventMixingTelemetrySource(source) { EventEvery = every };
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
            };
        }

        SyntheticTelemetrySource source = new();

        if (named.ContainsKey("interval-ms"))
        {
            TimeSpan interval = IntervalFrom(named, defaultMilliseconds: 1000);

            return new SyntheticTelemetrySource
            {
                IdleInterval = interval,
                PrintingInterval = interval,
            };
        }

        return source;
    }

    private static TimeSpan IntervalFrom(Dictionary<string, string> named, int defaultMilliseconds)
    {
        return named.TryGetValue("interval-ms", out string? value) && int.TryParse(value, out int milliseconds)
            ? TimeSpan.FromMilliseconds(milliseconds)
            : TimeSpan.FromMilliseconds(defaultMilliseconds);
    }
}
