using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Cameras;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Streams the sidecar holds for cameras Homespool no longer has, and what removes them - against a
/// sidecar that answers.
/// </summary>
/// <remarks>
/// <para>
/// <b>An orphan is not inert.</b> For a camera attached to this machine, the device reads as free once
/// its row is gone, and re-adding it puts a second stream on one device node. So each case asserts on
/// what the sidecar was left holding, not on what the page said.
/// </para>
/// <para>
/// <b>Each test starts its own host</b>, because one of them has to have the sidecar populated before
/// the reconciler's startup sweep runs.
/// </para>
/// </remarks>
public sealed class CameraStreamSweepTests : IAsyncLifetime
{
    private const string BoundSource = "rtsp://192.0.2.10/live";
    private const string KeptSource = "rtsp://192.0.2.11/live";
    private const string NewSource = "rtsp://192.0.2.12/live";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("camera-stream-sweep");
    private FakeGo2Rtc _sidecar = null!;
    private HomespoolFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _sidecar = await FakeGo2Rtc.StartAsync();

        _factory = new HomespoolFactory(_scratch);
        _sidecar.ApplyTo(_factory);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _sidecar.DisposeAsync();

        _scratch.Dispose();
    }

    /// <summary>
    /// Removing a printer takes its cameras by cascade and never tells the sidecar. The next camera
    /// saved removes the stream the removed one left - and only that one.
    /// </summary>
    [Fact]
    public async Task ACameraRemovedWithItsPrinterHasItsStreamRemovedByTheNextSave()
    {
        Start();

        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "sweep-printer@example.com");

        using (client)
        {
            Printer printer = await SeedPrinterAsync(user);
            Camera bound = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "bound", BoundSource, printer.Uuid);
            Camera kept = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "kept", KeptSource);

            await RemovePrinterAsync(user, printer);

            _sidecar.Streams.Should().ContainKey(
                bound.Uuid.ToString(),
                "the printer's removal must have left the stream behind, or its absence below proves nothing");

            Camera added = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "added", NewSource);

            _sidecar.Streams.Keys.Should().BeEquivalentTo(
                [kept.Uuid.ToString(), added.Uuid.ToString()],
                "the removed camera's stream goes, and the cameras that still exist keep theirs");
        }
    }

    /// <summary>
    /// A stream Homespool did not name is somebody else's, and a save leaves it where it is - a
    /// uuid in any form but the one Homespool writes included.
    /// </summary>
    [Fact]
    public async Task AStreamHomespoolDidNotNameSurvivesASave()
    {
        string upperCaseUuid = Guid.NewGuid().ToString("D").ToUpperInvariant();

        _sidecar.HoldStream("garage", KeptSource);
        _sidecar.HoldStream(upperCaseUuid, BoundSource);

        Start();

        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "sweep-foreign@example.com");

        using (client)
        {
            Camera added = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "added", NewSource);

            _sidecar.Streams.Keys.Should().BeEquivalentTo(
                ["garage", upperCaseUuid, added.Uuid.ToString()],
                "only a canonical uuid is a name Homespool writes, so nothing else is Homespool's to remove");

            // Asked of the requests as well as the result: a delete is by the canonical name, so one
            // aimed at the upper-case stream would leave it in place and still be logged as a removal.
            _sidecar.Requests.Should().NotContain(
                request => request.StartsWith("DELETE", StringComparison.Ordinal),
                "nothing the sidecar held was Homespool's, so nothing should have been asked to go");
        }
    }

    /// <summary>
    /// A stream an earlier camera left is removed at startup, even when there are no cameras left at
    /// all - the removal of the last camera is the one the sweep would otherwise never see.
    /// </summary>
    [Fact]
    public async Task AnOrphanIsRemovedAtStartupWithNoCamerasLeft()
    {
        string orphan = Guid.NewGuid().ToString("D");

        _sidecar.HoldStream(orphan, BoundSource);
        _sidecar.HoldStream("garage", KeptSource);

        Start();

        await _factory.Services.GetServices<IHostedService>()
                      .OfType<CameraStreamReconciler>()
                      .Single()
                      .ExecuteTask!
                      .WaitAsync(TestContext.Current.CancellationToken);

        _sidecar.Streams.Keys.Should().BeEquivalentTo(
            ["garage"],
            "the orphan is Homespool's by its name, and the other stream is not");
    }

    private void Start()
    {
        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();
    }

    private async Task<Printer> SeedPrinterAsync(HSUser user)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        int teamId = await context.TeamMembers
                                  .Where(member => member.UserId == user.Id)
                                  .Select(member => member.TeamId)
                                  .FirstAsync(TestContext.Current.CancellationToken);

        Printer printer = new() { Uuid = Guid.NewGuid(), TeamId = teamId, Name = "Removed printer" };
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return printer;
    }

    private async Task RemovePrinterAsync(HSUser user, Printer printer)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        string? name = await scope.ServiceProvider.GetRequiredService<PrinterRemovalService>()
                                  .RemovePrinterAsync(printer.Uuid, Caller.Unscoped(user.Id), TestContext.Current.CancellationToken);

        name.Should().NotBeNull("the printer must actually have been removed");
    }
}
