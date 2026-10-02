using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Cameras;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// What a camera saved through the real Cameras page leaves the stream server holding, and what the
/// page says about it - against a sidecar that answers.
/// </summary>
/// <remarks>
/// <para>
/// <b>The three outcomes of a save are three different facts</b>: the sidecar would not take the
/// source, it took it and the camera sent nothing, or it took it and a picture came back. Only the
/// last is success, and the first two still save - so each is told apart here by what the page says,
/// and by what the sidecar was left holding.
/// </para>
/// <para>
/// <b>The camera is known to the sidecar by its source with the password put back in</b>, so a save
/// that handed over the address alone would reach a camera that is not there and read as silent.
/// </para>
/// </remarks>
public sealed class CameraRegistrationTests : IAsyncLifetime
{
    private const string Address = "rtsp://192.0.2.1/live";
    private const string Source = "rtsp://cam:camera-secret@192.0.2.1/live"; // betterleaks:allow - a test fixture for a camera that does not exist

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("camera-registration");
    private FakeGo2Rtc _sidecar = null!;
    private HomespoolFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _sidecar = await FakeGo2Rtc.StartAsync();

        _factory = new HomespoolFactory(_scratch);
        _sidecar.ApplyTo(_factory);

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _sidecar.DisposeAsync();

        _scratch.Dispose();
    }

    /// <summary>
    /// A camera that sends a picture is saved as working, registered under its uuid with its password
    /// restored, and its codecs are already known before any page asks how to watch it.
    /// </summary>
    [Fact]
    public async Task ACameraThatSendsAPictureIsSavedAsWorking()
    {
        _sidecar.AddCamera(Source, FakeCamera.H264);

        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-working@example.com");

        using (client)
        {
            Camera camera = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "working", Source);

            (await CameraPage.AlertAsync(client, "success")).Should().Be(
                CameraPage.Localised(_factory, "Cameras_SavedWithPicture"));

            camera.Source.Should().Be(Address, "the password is stored apart from the address");
            _sidecar.Streams.Should().Contain(camera.Uuid.ToString(), Source,
                                              "the sidecar is handed the source whole, password and all");

            KnownCodecs? known = _factory.Services.GetRequiredService<CameraLiveAvailability>().Remembered(camera.Uuid);
            known.Should().NotBeNull("the save is the moment the camera is known to answer, so the probe is spent there");
            known!.Codecs.Should().BeEquivalentTo(["H264"]);
            _sidecar.Describes.Should().Be(1, "the codecs were asked over RTSP, once");
        }
    }

    /// <summary>
    /// A camera the sidecar registers but that sends nothing is saved, and the page says the picture
    /// did not come - with the hint for a network camera rather than an attached one.
    /// </summary>
    [Fact]
    public async Task ACameraThatSendsNothingIsSavedWithTheNetworkHint()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-silent@example.com");

        using (client)
        {
            Camera camera = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "silent", Source);

            (await CameraPage.AlertAsync(client, "warning")).Should().Be(
                CameraPage.Localised(_factory, "Cameras_NoPictureNetwork"));

            _sidecar.Streams.Should().ContainKey(camera.Uuid.ToString(),
                                                 "a camera switched off while it is set up is still worth keeping");
            _factory.Services.GetRequiredService<CameraLiveAvailability>().Remembered(camera.Uuid).Should().BeNull(
                "a camera that sent nothing has no codecs to remember");
        }
    }

    /// <summary>
    /// A source the sidecar refuses is still saved, and the page says the stream server would not
    /// take it rather than that the camera is silent.
    /// </summary>
    [Fact]
    public async Task ASourceTheSidecarRefusesIsSavedWithTheRefusal()
    {
        _sidecar.RefuseSource(Source);

        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-refused@example.com");

        using (client)
        {
            Camera camera = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "refused", Source);

            (await CameraPage.AlertAsync(client, "warning")).Should().Be(
                CameraPage.Localised(_factory, "Cameras_StreamServerRefused"));

            _sidecar.Streams.Should().NotContainKey(camera.Uuid.ToString());
        }
    }

    /// <summary>
    /// A sidecar that takes the source but cannot save it to go2rtc.yaml is not the address's fault,
    /// and the page must not send anybody to check the address - it says where the fault is instead.
    /// </summary>
    [Fact]
    public async Task AnUnsavedSidecarConfigurationIsNotBlamedOnTheAddress()
    {
        _sidecar.FailConfigurationWrites();

        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-unsaved@example.com");

        using (client)
        {
            await CameraPage.AddNetworkCameraAsync(_factory, client, user, "unsaved", Source);

            (await CameraPage.AlertAsync(client, "warning")).Should().Be(
                CameraPage.Localised(_factory, "Cameras_StreamServerConfigurationNotSaved"));
        }
    }

    /// <summary>
    /// Saving a camera without changing its source leaves the sidecar's stream alone: a replacement
    /// would hand anybody already watching a second reader on the camera. Changing the source does
    /// replace it.
    /// </summary>
    [Fact]
    public async Task ASaveThatKeepsTheSourceLeavesTheStreamAlone()
    {
        _sidecar.AddCamera(Source, FakeCamera.H264);

        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-renamed@example.com");

        using (client)
        {
            Camera camera = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "before", Source);
            Registrations().Should().Be(1, "the camera must have been registered, or leaving it alone proves nothing");

            await CameraPage.EditAsync(client, camera.Uuid, "after", Source);

            Registrations().Should().Be(1, "the source did not change, so the stream did not need replacing");
            _sidecar.Streams.Should().Contain(camera.Uuid.ToString(), Source);

            const string Moved = "rtsp://cam:camera-secret@192.0.2.2/live"; // betterleaks:allow - a test fixture for a camera that does not exist
            await CameraPage.EditAsync(client, camera.Uuid, "after", Moved);

            Registrations().Should().Be(2);
            _sidecar.Streams.Should().Contain(camera.Uuid.ToString(), Moved);
        }

        int Registrations()
        {
            return _sidecar.Requests.Count(request => request.StartsWith("PUT /api/streams", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Removing a camera removes its stream from the sidecar - by <c>src</c>, the one form of the
    /// delete that actually deletes.
    /// </summary>
    [Fact]
    public async Task RemovingACameraRemovesItsStream()
    {
        _sidecar.AddCamera(Source, FakeCamera.H264);

        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "camera-removed@example.com");

        using (client)
        {
            Camera camera = await CameraPage.AddNetworkCameraAsync(_factory, client, user, "removed", Source);
            _sidecar.Streams.Should().ContainKey(camera.Uuid.ToString(), "the stream must exist, or its absence proves nothing");

            await CameraPage.DeleteAsync(client, camera.Uuid);

            _sidecar.Streams.Should().NotContainKey(
                camera.Uuid.ToString(),
                "a stream left behind holds its camera - for an attached one, a device the picker then offers again");
        }
    }
}
