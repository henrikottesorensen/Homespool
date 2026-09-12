using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Pages;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// That the home page's tile drop refuses an oversized upload while it is arriving, rather than
/// after - the same property <see cref="FilesPageUploadLimitTests"/> pins for the Files page.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same ordering, asserted separately because nothing makes the two pages agree.</b> The
/// bound comes from an attribute declared per class, and no build or test fails when a page that
/// takes an upload does not carry it - which is how this handler ran on Kestrel's default ceiling
/// while the dialog advertised the configured cap. A test that only covered the Files page would
/// stay green through exactly that.
/// </para>
/// <para>
/// <b>A list is what this handler binds, so both halves of the bound matter here.</b> The filter
/// sets a per-section multipart limit and the server's ceiling on the whole body, and only the first
/// is observable through <c>TestServer</c> - so the third test pins that an oversized file is refused
/// beside small ones, and the sum is left to the filter's own reading. That asymmetry is the one way
/// this page differs from the Files page, where a single file makes the two indistinguishable.
/// </para>
/// <para>
/// The cap is turned down through configuration rather than the body turned up, so the test costs
/// kilobytes instead of the 512 MiB the shipped default would need.
/// </para>
/// </remarks>
public sealed class TileDropUploadLimitTests : IAsyncLifetime
{
    private const int CapBytes = 200_000;

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("tiledropcap");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);
        _factory.ConfigurationOverrides["PrintFiles:MaxUploadBytes"] =
            CapBytes.ToString(CultureInfo.InvariantCulture);

        _ = _factory.Server;

        // Without this the setup gate redirects every request to /setup and the page never renders.
        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }

    [Fact]
    public async Task AFileUnderTheCapIsStillAccepted()
    {
        // The half that proves the refusals below are the cap doing its job rather than the handler
        // being broken - a limit that refuses everything would pass the other tests on its own.
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "dropundercap@example.com");

        Guid uuid = SeedPrinter(user.Id, "Under Cap");

        using HttpResponseMessage response = await PostDropAsync(client, uuid, ("small.gcode", 512));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        client.Dispose();
    }

    [Fact]
    public async Task AFileOverTheCapIsRefusedWhileItArrives()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "dropovercap@example.com");

        Guid uuid = SeedPrinter(user.Id, "Over Cap");

        // Well past the cap plus the form overhead, so it is the body that trips it.
        using HttpResponseMessage response = await PostDropAsync(client, uuid, ("huge.gcode", CapBytes * 4));

        ShouldBeRefused(response);

        client.Dispose();
    }

    /// <summary>
    /// An oversized file is refused even when it arrives beside files that are fine, so a multi-file
    /// drop cannot carry one past the bound in company. The all-small post in the same test is what
    /// stops this passing merely because multi-file drops were broken.
    /// </summary>
    /// <remarks>
    /// This is the per-section half of the bound - <c>MultipartBodyLengthLimit</c>, which applies to
    /// each file rather than to the request. The other half, the server's own ceiling on the sum of
    /// them, cannot be asserted here: <c>TestServer</c> exposes no request-body-size feature for the
    /// filter to set, so only Kestrel enforces it and only in production.
    /// </remarks>
    [Fact]
    public async Task AnOversizedFileIsRefusedEvenBesideSmallOnes()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "dropsum@example.com");

        Guid uuid = SeedPrinter(user.Id, "Sum Of Parts");

        using HttpResponseMessage small = await PostDropAsync(client,
                                                              uuid,
                                                              ("first.gcode", 512),
                                                              ("second.gcode", 512));

        small.StatusCode.Should().Be(HttpStatusCode.Redirect,
                                     "two files that are both inside the cap are an ordinary drop");

        using HttpResponseMessage mixed = await PostDropAsync(client,
                                                              uuid,
                                                              ("first.gcode", 512),
                                                              ("huge.gcode", CapBytes * 4));

        ShouldBeRefused(mixed);

        client.Dispose();
    }

    /// <summary>
    /// Deliberately a range rather than one code. Which of the two bounds trips first depends on the
    /// server: TestServer has no request-body-size feature, so the multipart limit refuses it as 400,
    /// where Kestrel's own ceiling answers 413. Pinning either would pass here and be wrong about
    /// production, and what matters to this test is the same for both.
    /// </summary>
    private static void ShouldBeRefused(HttpResponseMessage response)
    {
        response.StatusCode.Should().NotBe(HttpStatusCode.Redirect,
                                           "a refused upload must not look like a stored one");
        ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification =
                         "MultipartFormDataContent takes ownership of the parts added to it and disposes them with itself, which the using declaration below does.")]
    private static async Task<HttpResponseMessage> PostDropAsync(HttpClient client,
                                                                 Guid uuid,
                                                                 params (string name, int bytes)[] files)
    {
        string page = await (await client.GetAsync("/", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using MultipartFormDataContent form = [];

        form.Add(new StringContent(AntiforgeryTestHelper.ExtractToken(page)), "__RequestVerificationToken");
        form.Add(new StringContent(uuid.ToString()), "uuid");

        // Upload only: the queue is not what is under test here, and a drop that also queues would
        // make a refusal ambiguous between the bound and a printer that would not take the job.
        form.Add(new StringContent(TileDrop.Upload), "action");

        foreach ((string name, int bytes) in files)
        {
            form.Add(new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('G', bytes)))),
                     "files",
                     name);
        }

        return await client.PostAsync("/?handler=Drop", form, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// One printer on the team registration already made for this user, so the access path the
    /// handler takes before it reads a file is the real one rather than a membership invented here.
    /// </summary>
    private Guid SeedPrinter(long userId, string name)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        int teamId = context.TeamMembers.First(member => member.UserId == userId).TeamId;

        Printer printer = new() { Uuid = Guid.NewGuid(), TeamId = teamId, Name = name };

        context.Printers.Add(printer);
        context.SaveChanges();

        return printer.Uuid;
    }
}
