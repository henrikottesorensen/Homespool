using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The attached-camera form, through the real pipeline, posting a device name nobody picked.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the rule and the calling of the rule are different things, and only one of
/// them had a test.</b> <c>LocalCameraDevices.CheckComposed</c> is covered directly, but every one of
/// those cases would keep passing with <c>CameraService</c>'s two calls to it deleted - so the suite
/// agreed the rule was right while saying nothing about whether anything asked it. That gap is what
/// this closes, and it is why the assertion is on the database rather than on a returned message:
/// what matters is that no camera was stored, whatever the page said.
/// </para>
/// <para>
/// <b>The sidecar credential is configured here deliberately.</b> Without it <c>CreateAsync</c>
/// refuses before it reaches the check at all, and this test would pass on a codebase that had no
/// check in it - the exact false pass it exists to rule out. Nothing contacts a sidecar: the save is
/// refused before anything is registered.
/// </para>
/// <para>
/// The device list is empty in a test - <c>LocalCameraDevices</c> reads a directory that exists only
/// in the container - so any device name is one this machine does not have. That is the same refusal
/// a forged name gets on a real machine, reached without needing hardware.
/// </para>
/// </remarks>
public sealed class AttachedCameraHandlerTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("attached-camera");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

        _factory.ConfigurationOverrides["Cameras:ApiUsername"] = "homespool";
        _factory.ConfigurationOverrides["Cameras:ApiPassword"] = "not-a-real-password"; // betterleaks:allow - nothing here contacts a sidecar

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }

    /// <summary>
    /// A device name carrying an ffmpeg argument is refused, and nothing is stored.
    /// </summary>
    /// <remarks>
    /// The stream server splits an <c>ffmpeg:</c> source on <c>#</c> and makes each fragment an
    /// argument, so this name is a way to add arguments to the process it runs. The form composes the
    /// source rather than accepting one, which is not by itself the protection: the device name it
    /// composes from arrives here.
    /// </remarks>
    [Fact]
    public async Task ADeviceNameCarryingAnFfmpegArgumentIsNotStored()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "admin@example.com", AdminBootstrap.AdminRole);

        int teamId = await TeamIdAsync(user);

        using HttpResponseMessage response = await PostAttachedAsync(
            client, teamId, "usb-camera-video-index0#raw=-i#raw=/etc/hostname");

        await AssertNoCameraStoredAsync("a device name is half of a command line the sidecar runs");
    }

    /// <summary>
    /// A capture size is the other half of the composed source, and reaches it unquoted.
    /// </summary>
    [Fact]
    public async Task AResolutionCarryingAnFfmpegArgumentIsNotStored()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "admin@example.com", AdminBootstrap.AdminRole);

        int teamId = await TeamIdAsync(user);

        using HttpResponseMessage response = await PostAttachedAsync(
            client, teamId, "usb-camera-video-index0", "640x480#raw=-i");

        await AssertNoCameraStoredAsync("a capture size is interpolated into the same string");
    }

    private async Task<int> TeamIdAsync(HSUser user)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        return await context.TeamMembers
                            .Where(member => member.UserId == user.Id)
                            .Select(member => member.TeamId)
                            .FirstAsync(TestContext.Current.CancellationToken);
    }

    private async Task AssertNoCameraStoredAsync(string because)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        (await context.Cameras.CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(0, because);
    }

    private static async Task<HttpResponseMessage> PostAttachedAsync(HttpClient client,
                                                                    int teamId,
                                                                    string device,
                                                                    string? resolution = null)
    {
        string page = await (await client.GetAsync("/Cameras", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using FormUrlEncodedContent form = new(
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
            new("name", "forged"),
            new("device", device),
            new("teamId", teamId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("resolution", resolution ?? string.Empty),
        ]);

        return await client.PostAsync("/Cameras?handler=AddAttached", form, TestContext.Current.CancellationToken);
    }
}
