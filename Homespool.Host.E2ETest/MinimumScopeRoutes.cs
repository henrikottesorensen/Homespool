using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;

using Homespool.FakePrinter;
using Homespool.Host.Cameras;
using Homespool.Host.Controllers;
using Homespool.Model;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Every route a personal access token can reach, with the least scope each needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>A row is a claim about intent, checked from both sides</b> by <see cref="MinimumScopeTests"/>:
/// the stated minimum is enough, and every capability in it is needed. Together they say the
/// minimum is exact for the way through the row arranges - nothing more, because a capability a
/// side branch needs is invisible to a row that does not arrange that branch.
/// </para>
/// <para>
/// <b>Where either of two capabilities will do, each is its own row</b>: stopping your own print
/// takes <see cref="Capability.Print"/> and stopping anyone's takes
/// <see cref="Capability.ControlPrinter"/>, and no single row could show both. The narrower row
/// names the wider capability as an alternative, so the token that has to be refused leaves it out.
/// </para>
/// <para>
/// <b>A minimum names no capability another in it already implies.</b> A token's scope is closed
/// over implications when it is stored, so <c>[Print, ViewPrinter]</c> mints the same token as
/// <c>[Print]</c>, and a row stating both claims a need the token cannot express.
/// </para>
/// </remarks>
public static class MinimumScopeRoutes
{
    /// <summary>A CORE One's <c>printer_type</c> - built with the LED strips, and reporting them.</summary>
    private const string CoreOne = "7.1.0";

    /// <summary>A recv-only H.264 offer, as a browser makes one.</summary>
    private const string OfferSdp = "v=0\r\no=browser 1 1 IN IP4 0.0.0.0\r\ns=-\r\nt=0 0\r\n" +
                                    "m=video 9 UDP/TLS/RTP/SAVPF 96\r\nc=IN IP4 0.0.0.0\r\na=mid:0\r\na=rtpmap:96 H264/90000\r\n" +
                                    "a=recvonly\r\n";

    private const string File = MinimumScopeWorld.FileName;

    public static IReadOnlyList<MinimumScopeRoute> All { get; } =
    [

        // Files are always the caller's own, so a membership neither grants nor withholds them: the
        // three file capabilities are about the credential, and only a token's scope narrows them.
        Route(typeof(PrintFileController), nameof(PrintFileController.List), null,
              [Capability.ViewOwnFiles],
              world => world.UploadAsync(),
              _ => Get("/api/v1/files"),
              HttpStatusCode.OK, ScopeRefusal.Forbidden),

        Route(typeof(PrintFileController), nameof(PrintFileController.Upload), "a new name",
              [Capability.UploadOwnFiles],
              world => world.EnsureOwnerAsync(),
              _ => Send(HttpMethod.Put, $"/api/v1/files/{File}", Gcode()),
              HttpStatusCode.OK, ScopeRefusal.Forbidden),

        Route(typeof(PrintFileController), nameof(PrintFileController.Upload), "overwriting",
              [Capability.ManipulateOwnFiles],
              world => world.UploadAsync(),
              _ => Send(HttpMethod.Put, $"/api/v1/files/{File}?overwrite=true", Gcode()),
              HttpStatusCode.OK, ScopeRefusal.Forbidden),

        Route(typeof(PrintFileController), nameof(PrintFileController.Download), null,
              [Capability.ViewOwnFiles],
              world => world.UploadAsync(),
              _ => Get($"/api/v1/files/{File}"),
              HttpStatusCode.OK, ScopeRefusal.Forbidden),

        Route(typeof(PrintFileController), nameof(PrintFileController.Rename), null,
              [Capability.ManipulateOwnFiles],
              world => world.UploadAsync(),
              _ => Send(HttpMethod.Patch, $"/api/v1/files/{File}", JsonContent.Create(new { name = "renamed.gcode" })),
              HttpStatusCode.OK, ScopeRefusal.Forbidden),

        Route(typeof(PrintFileController), nameof(PrintFileController.Delete), null,
              [Capability.ManipulateOwnFiles],
              world => world.UploadAsync(),
              _ => Send(HttpMethod.Delete, $"/api/v1/files/{File}"),
              HttpStatusCode.NoContent, ScopeRefusal.Forbidden),

        // The slicer's key is UploadOwnFiles and Print, and nothing else: docs/capabilities.md, "A
        // scope for a slicer".
        Route(typeof(OctoPrintCompatController), nameof(OctoPrintCompatController.Version), null,
              [Capability.ViewPrinter],
              world => world.AddPrinterAsync(),
              world => Get($"/compat/octoprint/{world.Printer}/api/version"),
              HttpStatusCode.OK, ScopeRefusal.Forbidden),

        Route(typeof(OctoPrintCompatController), nameof(OctoPrintCompatController.Upload), "upload",
              [Capability.ViewPrinter, Capability.UploadOwnFiles],
              world => world.AddPrinterAsync(),
              world => Send(HttpMethod.Post, $"/compat/octoprint/{world.Printer}/api/files/local", SlicerUpload(print: false)),
              HttpStatusCode.Created, ScopeRefusal.Forbidden),

        Route(typeof(OctoPrintCompatController), nameof(OctoPrintCompatController.Upload), "upload and print",
              [Capability.UploadOwnFiles, Capability.Print],
              world => world.AddPrinterAsync(),
              world => Send(HttpMethod.Post, $"/compat/octoprint/{world.Printer}/api/files/local", SlicerUpload(print: true)),
              HttpStatusCode.Created, ScopeRefusal.Forbidden),

        Route(typeof(PrinterAppController), nameof(PrinterAppController.RegisterPrinter), null,
              [Capability.ManagePrinter],
              world => world.RegisterPrinterAsync(),
              world => Send(HttpMethod.Post, "/api/v1/printers/register",
                            JsonContent.Create(new { name = "Claimed", location = "Bench", code = world.ClaimCode })),
              HttpStatusCode.Created, ScopeRefusal.Forbidden),

        // Any valid token, whatever its scope, so a script can check one works.
        Route(typeof(PrinterAppController), nameof(PrinterAppController.GetCurrentUser), null,
              [],
              world => world.EnsureOwnerAsync(),
              _ => Get("/api/v1/user"),
              HttpStatusCode.OK, ScopeRefusal.None),

        Route(typeof(PrinterAppController), nameof(PrinterAppController.ListPrinters), null,
              [Capability.ViewPrinter],
              world => world.AddPrinterAsync(),
              _ => Get("/api/v1/printers"),
              HttpStatusCode.OK, ScopeRefusal.EmptyList),

        Route(typeof(PrinterAppController), nameof(PrinterAppController.GetPrinter), null,
              [Capability.ViewPrinter],
              world => world.AddPrinterAsync(),
              world => Get($"/api/v1/printers/{world.Printer}"),
              HttpStatusCode.OK, ScopeRefusal.Forbidden),

        Route(typeof(PrinterAppController), nameof(PrinterAppController.PatchPrinter), null,
              [Capability.ManagePrinter],
              world => world.AddPrinterAsync(),
              world => Send(HttpMethod.Patch, $"/api/v1/printers/{world.Printer}", JsonContent.Create(new { name = "Renamed" })),
              HttpStatusCode.OK, ScopeRefusal.Forbidden),

        Route(typeof(PrinterController), nameof(PrinterController.SendFile), null,
              [Capability.Print],
              async world =>
              {
                  await world.ConnectPrinterAsync();
                  await world.UploadAsync();
              },
              world => Send(HttpMethod.Post, $"/api/v1/printers/{world.Printer}/files", JsonContent.Create(new { name = File })),
              HttpStatusCode.NoContent, ScopeRefusal.Forbidden),

        Route(typeof(PrinterController), nameof(PrinterController.Storage), null,
              [Capability.ControlPrinter],
              world => world.ConnectPrinterAsync(),
              world => Send(HttpMethod.Post, $"/api/v1/printers/{world.Printer}/storage/usb"),
              HttpStatusCode.OK, ScopeRefusal.Forbidden),

        Route(typeof(PrinterController), nameof(PrinterController.Pause), null,
              [Capability.ControlPrinter],
              world => world.ConnectPrinterAsync(device => device.StartPrint(jobId: 7)),
              world => Command(world, "pause"),
              HttpStatusCode.NoContent, ScopeRefusal.Forbidden),

        Route(typeof(PrinterController), nameof(PrinterController.Resume), null,
              [Capability.ControlPrinter],
              world => world.ConnectPrinterAsync(device =>
              {
                  device.StartPrint(jobId: 7);
                  device.TryPause();
              }),
              world => Command(world, "resume"),
              HttpStatusCode.NoContent, ScopeRefusal.Forbidden),

        // A print with no open row is nobody's to withdraw, so stopping it is running the machine.
        Route(typeof(PrinterController), nameof(PrinterController.Stop), "a print started at the panel",
              [Capability.ControlPrinter],
              world => world.ConnectPrinterAsync(device => device.StartPrint(jobId: 7)),
              world => Command(world, "stop"),
              HttpStatusCode.NoContent, ScopeRefusal.Forbidden),

        Route(typeof(PrinterController), nameof(PrinterController.Stop), "your own print",
              [Capability.Print],
              async world =>
              {
                  await world.ConnectPrinterAsync(device => device.StartPrint(jobId: 7));
                  await world.AddOwnOpenPrintAsync();
              },
              world => Command(world, "stop"),
              HttpStatusCode.NoContent, ScopeRefusal.Forbidden,
              alternatives: [Capability.ControlPrinter]),

        Route(typeof(PrinterController), nameof(PrinterController.Stop), "a teammate's print",
              [Capability.ControlPrinter],
              async world =>
              {
                  await world.ConnectPrinterAsync(device => device.StartPrint(jobId: 7));
                  await world.AddTeammatesOpenPrintAsync();
              },
              world => Command(world, "stop"),
              HttpStatusCode.NoContent, ScopeRefusal.Forbidden),

        // Readying and standing down are Print, deliberately: somebody able to queue work must be
        // able to start it.
        Route(typeof(PrinterController), nameof(PrinterController.Unready), null,
              [Capability.Print],
              world => world.ConnectPrinterAsync(device => device.ForceState(DeviceState.Ready)),
              world => Command(world, "unready"),
              HttpStatusCode.NoContent, ScopeRefusal.Forbidden),

        Route(typeof(PrinterController), nameof(PrinterController.Idle), null,
              [Capability.ControlPrinter],
              world => world.ConnectPrinterAsync(device => device.ForceState(DeviceState.Finished)),
              world => Command(world, "idle"),
              HttpStatusCode.NoContent, ScopeRefusal.Forbidden),

        Route(typeof(PrinterController), nameof(PrinterController.Lighting), null,
              [Capability.ControlPrinter],
              world => world.ConnectPrinterAsync(printerType: CoreOne),
              world => Send(HttpMethod.Put, $"/api/v1/printers/{world.Printer}/command/lighting",
                            JsonContent.Create(new { intensity = 33 })),
              HttpStatusCode.NoContent, ScopeRefusal.Forbidden),

        Route(typeof(PrintQueueController), nameof(PrintQueueController.List), null,
              [Capability.ViewPrinter, Capability.ViewQueue],
              world => world.AddPrinterAsync(),
              world => Get($"/api/v1/printers/{world.Printer}/queue"),
              HttpStatusCode.OK, ScopeRefusal.Forbidden),

        Route(typeof(PrintQueueController), nameof(PrintQueueController.Enqueue), null,
              [Capability.Print],
              async world =>
              {
                  await world.AddPrinterAsync();
                  await world.UploadAsync();
              },
              world => Send(HttpMethod.Post, $"/api/v1/printers/{world.Printer}/queue", JsonContent.Create(new { name = File })),
              HttpStatusCode.Created, ScopeRefusal.Forbidden),

        // Reordering moves everybody's work - there is one queue - so it is operating the printer.
        Route(typeof(PrintQueueController), nameof(PrintQueueController.Move), null,
              [Capability.ControlPrinter],
              async world =>
              {
                  await world.AddPrinterAsync();
                  await world.EnqueueAsync();
              },
              world => Send(HttpMethod.Patch, $"/api/v1/printers/{world.Printer}/queue/{world.QueueEntry}",
                            JsonContent.Create(new { position = 0 })),
              HttpStatusCode.NoContent, ScopeRefusal.Forbidden),

        Route(typeof(PrintQueueController), nameof(PrintQueueController.Cancel), "your own entry",
              [Capability.Print],
              async world =>
              {
                  await world.AddPrinterAsync();
                  await world.EnqueueAsync();
              },
              world => Send(HttpMethod.Delete, $"/api/v1/printers/{world.Printer}/queue/{world.QueueEntry}"),
              HttpStatusCode.NoContent, ScopeRefusal.Forbidden,
              alternatives: [Capability.ControlPrinter]),

        Route(typeof(PrintQueueController), nameof(PrintQueueController.Cancel), "a teammate's entry",
              [Capability.ControlPrinter],
              async world =>
              {
                  await world.AddPrinterAsync();
                  await world.EnqueueAsTeammateAsync();
              },
              world => Send(HttpMethod.Delete, $"/api/v1/printers/{world.Printer}/queue/{world.QueueEntry}"),
              HttpStatusCode.NoContent, ScopeRefusal.Forbidden),

        Route(typeof(PrintJobController), nameof(PrintJobController.List), null,
              [Capability.ViewPrinter, Capability.ViewHistory],
              async world =>
              {
                  await world.AddPrinterAsync();
                  await world.AddFinishedPrintAsync();
              },
              world => Get($"/api/v1/printers/{world.Printer}/jobs"),
              HttpStatusCode.OK, ScopeRefusal.Forbidden),

        Route(typeof(PrintJobController), nameof(PrintJobController.Reprint), null,
              [Capability.ViewHistory, Capability.Print],
              async world =>
              {
                  await world.AddPrinterAsync();
                  await world.AddFinishedPrintAsync();
              },
              world => Send(HttpMethod.Post, $"/api/v1/printers/{world.Printer}/jobs/{world.Print}/reprint"),
              HttpStatusCode.Created, ScopeRefusal.Forbidden),

        Route(typeof(PrinterTelemetryController), nameof(PrinterTelemetryController.Get), null,
              [Capability.ViewPrinter],
              world => world.AddPrinterAsync(),
              world => Get($"/api/v1/printers/{world.Printer}/telemetry"),
              HttpStatusCode.OK, ScopeRefusal.Forbidden),

        Route(typeof(PrinterTelemetryController), nameof(PrinterTelemetryController.Temperatures), null,
              [Capability.ViewPrinter],
              world => world.AddPrinterAsync(),
              world => Get($"/api/v1/printers/{world.Printer}/telemetry/temperatures"),
              HttpStatusCode.OK, ScopeRefusal.Forbidden),

        Route(typeof(CameraController), nameof(CameraController.List), null,
              [Capability.ViewCamera],
              world => world.AddCameraAsync(),
              _ => Get("/api/v1/cameras"),
              HttpStatusCode.OK, ScopeRefusal.EmptyList),

        // A camera sending nothing has no current frame, and a stale one is never served - an answer
        // from past the gate, and one that does not race the sidecar's first picture.
        Route(typeof(CameraController), nameof(CameraController.Frame), null,
              [Capability.ViewCamera],
              world => world.AddCameraAsync(FakeCamera.H264 with { Producing = false }),
              world => Get($"/api/v1/cameras/{world.Camera}/frame"),
              HttpStatusCode.NoContent, ScopeRefusal.Forbidden),

        Route(typeof(CameraController), nameof(CameraController.Live), null,
              [Capability.ViewCamera],
              world => world.AddCameraAsync(),
              world => Get($"/api/v1/cameras/{world.Camera}/live"),
              HttpStatusCode.OK, ScopeRefusal.Forbidden),

        Route(typeof(CameraController), nameof(CameraController.Stream), null,
              [Capability.ViewCamera],
              world => world.AddCameraAsync(MinimumScopeWorld.MjpegCamera),
              world => Get($"/api/v1/cameras/{world.Camera}/stream.mjpeg"),
              HttpStatusCode.OK, ScopeRefusal.Forbidden),

        // Ending your own view asks for nothing: it only ever gives something up.
        Route(typeof(CameraController), nameof(CameraController.StopStream), null,
              [],
              world => world.OpenViewAsync(),
              world => Send(HttpMethod.Delete, $"/api/v1/cameras/{world.Camera}/stream/{world.View}"),
              HttpStatusCode.NoContent, ScopeRefusal.None),

        Route(typeof(CameraController), nameof(CameraController.WebRtc), null,
              [Capability.ViewCamera],
              world => world.AddCameraAsync(),
              world => Send(HttpMethod.Post, $"/api/v1/cameras/{world.Camera}/webrtc",
                            JsonContent.Create(new WebRtcDescription("offer", OfferSdp))),
              HttpStatusCode.OK, ScopeRefusal.Forbidden),
    ];

    /// <summary>The row called <paramref name="name"/>.</summary>
    public static MinimumScopeRoute Named(string name)
    {
        return All.Single(route => route.Name == name);
    }

    private static MinimumScopeRoute Route(Type controller,
                                           string action,
                                           string? variant,
                                           IReadOnlyList<Capability> minimum,
                                           Func<MinimumScopeWorld, Task> arrange,
                                           Func<MinimumScopeWorld, HttpRequestMessage> request,
                                           HttpStatusCode passes,
                                           ScopeRefusal refused,
                                           IReadOnlyList<Capability>? alternatives = null)
    {
        return new MinimumScopeRoute(controller, action, variant, minimum, arrange, request, passes, refused, alternatives ?? []);
    }

    private static HttpRequestMessage Get(string url)
    {
        return new HttpRequestMessage(HttpMethod.Get, url);
    }

    private static HttpRequestMessage Send(HttpMethod method, string url, HttpContent? content = null)
    {
        return new HttpRequestMessage(method, url) { Content = content };
    }

    private static HttpRequestMessage Command(MinimumScopeWorld world, string act)
    {
        return Send(HttpMethod.Put, $"/api/v1/printers/{world.Printer}/command/{act}");
    }

    private static StreamContent Gcode()
    {
        return new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("G28 ; home\n")));
    }

    /// <summary>The body PrusaSlicer posts: the print flag, the parent directory, and the file.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification =
                         "Ownership of every part passes to the MultipartFormDataContent returned, which disposes them with itself; the request it is sent in disposes that.")]
    private static MultipartFormDataContent SlicerUpload(bool print)
    {
        MultipartFormDataContent body = new()
        {
            { new StringContent(print ? "true" : "false"), "print" },
            { new StringContent(string.Empty), "path" },
        };

        body.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("G28 ; home\n")), "file", File);

        return body;
    }
}

/// <summary>How a route answers a token whose scope is missing a capability it needs.</summary>
public enum ScopeRefusal
{
    /// <summary>The route needs no capability, so nothing can be missing.</summary>
    None,

    /// <summary>403, the scope refusal.</summary>
    Forbidden,

    /// <summary>200 with an empty list - a listing narrows rather than refuses.</summary>
    EmptyList,
}

/// <summary>
/// One way through one token-reachable action: the least scope that gets it past every capability
/// check, and what it answers with that scope and with everything but each part of it.
/// </summary>
/// <param name="Controller">The controller declaring the action.</param>
/// <param name="Action">The action, by <c>nameof</c>, so a rename breaks the build rather than the guard.</param>
/// <param name="Variant">Which way through, where an action has more than one; <c>null</c> where it has one.</param>
/// <param name="Minimum">
/// The intended least scope, <b>stated rather than discovered</b> - from <c>docs/capabilities.md</c>,
/// the <see cref="Capability"/> remarks and the action's own documentation. Probing the code for it
/// would accept whatever the code demands, which is the thing under test.
/// </param>
/// <param name="Arrange">What has to exist before the request can get past the gate.</param>
/// <param name="Request">The request, built against what was arranged.</param>
/// <param name="Passes">What the route answers with <paramref name="Minimum"/> - the status past every check, in this arrangement.</param>
/// <param name="Refused">What it answers a token holding everything but one capability of <paramref name="Minimum"/>.</param>
/// <param name="Alternatives">
/// Capabilities that get through this way on their own, in place of <paramref name="Minimum"/> -
/// <see cref="Capability.ControlPrinter"/> withdraws anybody's work, so it also withdraws your own.
/// Each such way through has a row of its own; this only keeps it out of the token that has to be refused.
/// </param>
public sealed record MinimumScopeRoute(Type Controller,
                                       string Action,
                                       string? Variant,
                                       IReadOnlyList<Capability> Minimum,
                                       Func<MinimumScopeWorld, Task> Arrange,
                                       Func<MinimumScopeWorld, HttpRequestMessage> Request,
                                       HttpStatusCode Passes,
                                       ScopeRefusal Refused,
                                       IReadOnlyList<Capability> Alternatives)
{
    /// <summary>The row's name in a test's display name, and the key the theories look it up by.</summary>
    public string Name => Variant is null ? $"{Controller.Name}.{Action}" : $"{Controller.Name}.{Action} ({Variant})";

    /// <summary>Whether the request goes to the slicer's compatibility shell, which takes the token in <c>X-Api-Key</c>.</summary>
    public bool SlicerHeader => Controller == typeof(OctoPrintCompatController);
}
