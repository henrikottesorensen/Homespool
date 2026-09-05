using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.FakePrinter;
using Homespool.Host.Accounts;
using Homespool.Host.Controllers;
using Homespool.Host.Printing;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// <c>Set ready</c> pressed at a printer that will not take it, driven the way a browser drives it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The writing rule was never the bug; the wiring was.</b> <c>OnPostReadyAsync</c> sent the
/// command and returned its own success string without ever looking at the answer, so a printer
/// answering <c>REJECTED "Can't set ready now"</c> produced a page saying the printer had been
/// marked ready. Nothing at the unit level sees that: the outcome is discarded inside the handler,
/// so only a real request through the real handler can catch it.
/// </para>
/// <para>
/// <b>It has to be a busy printer to be interesting.</b> Firmware takes the flag from <c>Idle</c>,
/// <c>Ready</c>, <c>Stopped</c> and <c>Finished</c> and refuses everything else
/// (<c>remote_print_ready</c>, printer_state.cpp:561-577), so a printing machine is the ordinary way
/// a person meets this - most often after queueing a file behind a print already running, which is
/// the case that produced it on hardware.
/// </para>
/// <para>
/// The fake answers this from firmware source rather than being told to fail, which is what makes
/// the refusal the printer's rather than the test's.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class SetReadyRefusalTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-setready-refusal-{Guid.NewGuid():N}.db");

    private HomespoolFactory _root = null!;
    private WebApplicationFactory<PrinterAppController> _factory = null!;

    public ValueTask InitializeAsync()
    {
        _root = new HomespoolFactory($"Data Source={_databasePath}");
        _factory = _root.WithWebHostBuilder(_ => { });

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _root.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// <b>The defect, reproduced.</b> A printing machine refuses the flag, and the page must say so
    /// in the printer's own words rather than claiming it worked.
    /// </summary>
    /// <remarks>
    /// Reverting the handler to discard the <c>CommandOutcome</c> fails here on the success
    /// assertion, which is the mutation that matters: every other assertion in this file would go on
    /// passing, because the command really is sent and really is answered.
    /// </remarks>
    [Fact]
    public async Task APrintingPrinterRefusesTheFlagAndThePageSaysSo()
    {
        (Guid uuid, HttpClient client, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();

        using (client)
        {
            // Act - a print is running, which is when somebody queues something behind it and reaches
            // for this button.
            fake.Device.StartPrint(jobId: 1, path: "/usb/A~1.BGC");

            string message = await PressSetReadyAsync(client, uuid);

            // Assert - firmware's own refusal, carried through verbatim.
            message.Should().Contain("set ready now",
                                     "the printer's words are the only honest account of why nothing happened");

            message.Should().NotContain("Marked ready",
                                        "reporting a refused command as success is the defect this covers");

            fake.Device.State.Should().Be(DeviceState.Printing, "a refused command changes nothing");

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// The other half, so the test above cannot pass by the page having stopped working: an idle
    /// printer takes the flag and is told it did.
    /// </summary>
    [Fact]
    public async Task AnIdlePrinterTakesTheFlag()
    {
        (Guid uuid, HttpClient client, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();

        using (client)
        {
            string message = await PressSetReadyAsync(client, uuid);

            message.Should().NotContain("set ready now");
            fake.Device.State.Should().Be(DeviceState.Ready);

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// <b>A finished print is the ordinary way a queue resumes</b>, and firmware's flag overrides
    /// that screen - so the button on a parked printer has to work.
    /// </summary>
    /// <remarks>
    /// This is the case the fake used to get wrong, refusing from <c>Finished</c> where hardware
    /// accepts. It would have failed against the double and passed against a printer, which is the
    /// worst way round for a fake to be wrong.
    /// </remarks>
    [Fact]
    public async Task APrinterParkedOnAFinishedPrintTakesTheFlag()
    {
        (Guid uuid, HttpClient client, FakePrinterClient fake, Task run) = await ConnectedPrinterAsync();

        using (client)
        {
            fake.Device.StartPrint(jobId: 1, path: "/usb/A~1.BGC");
            fake.Device.FinishPrint().Should().BeTrue();

            string message = await PressSetReadyAsync(client, uuid);

            message.Should().NotContain("set ready now",
                                        "the flag is exactly how a person says they cleared the bed");

            fake.Device.State.Should().Be(DeviceState.Ready);

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// Posts the form the page renders and returns whatever the redirect target then says, which is
    /// where <c>TempData</c> puts the answer.
    /// </summary>
    private async Task<string> PressSetReadyAsync(HttpClient client, Guid uuid)
    {
        string page = await GetDetailAsync(client, uuid);

        Dictionary<string, string> fields = new()
        {
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page),
        };

        using FormUrlEncodedContent body = new(fields);
        using HttpResponseMessage posted = await client.PostAsync($"/Printers/Detail/{uuid}?handler=Ready", body,
                                                                  TestContext.Current.CancellationToken);

        posted.StatusCode.Should().Be(HttpStatusCode.Redirect);

        return await GetDetailAsync(client, uuid);
    }

    private async Task<string> GetDetailAsync(HttpClient client, Guid uuid)
    {
        using HttpResponseMessage response = await client.GetAsync($"/Printers/Detail/{uuid}",
                                                                   TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// An enrolled, connected printer whose owner is signed in and allowed to ready it from here.
    /// </summary>
    private async Task<(Guid uuid, HttpClient client, FakePrinterClient fake, Task run)> ConnectedPrinterAsync()
    {
        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        FakePrinterClient fake = new(identity, TimeProvider.System, FastTelemetry()) { Token = token };
        await fake.ConnectAsync(ConnectAsync, TestContext.Current.CancellationToken);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);

        (await WaitUntilAsync(() => Task.FromResult(
                                  _factory.Services.GetRequiredService<PrinterConnectionRegistry>().IsConnected(printerId)),
                              TimeSpan.FromSeconds(10)))
            .Should().BeTrue("the command path needs a live socket");

        Guid uuid;

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

            Printer printer = await context.Printers.SingleAsync(candidate => candidate.Id == printerId,
                                                                 TestContext.Current.CancellationToken);

            // The control is absent rather than refusing when this is off, so the page would render no
            // form to post and this suite would be testing nothing.
            printer.RemoteReadyAllowed = true;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            uuid = printer.Uuid;
        }

        HSUser owner = await EnrolmentFlowHelper.FindUserAsync(_factory, userId);

        return (uuid, await EnrolmentFlowHelper.SignInAsAsync(_factory, owner), fake, run);
    }

    private static FakePrinterOptions FastTelemetry()
    {
        return new FakePrinterOptions
        {
            TelemetrySource = new SyntheticTelemetrySource
            {
                IdleInterval = TimeSpan.FromMilliseconds(200),
                PrintingInterval = TimeSpan.FromMilliseconds(200),
            },
        };
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static async Task EndRunAsync(FakePrinterClient fake, Task run)
    {
        await fake.DisposeAsync();

        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // The run loop ends by cancellation; that is how it stops, not a failure.
        }
        catch (WebSocketException)
        {
            // Same for the socket going away underneath it.
        }
    }

    private async Task<WebSocket> ConnectAsync(FakePrinterConnectRequest request, CancellationToken cancellationToken)
    {
        WebSocketClient client = _factory.Server.CreateWebSocketClient();
        client.SubProtocols.Add(request.SubProtocol);
        client.ConfigureRequest = httpRequest =>
        {
            foreach (KeyValuePair<string, string> header in request.Headers)
            {
                httpRequest.Headers[header.Key] = header.Value;
            }
        };

        return await client.ConnectAsync(PrinterListener.WebSocketUri(_factory), cancellationToken);
    }
}
