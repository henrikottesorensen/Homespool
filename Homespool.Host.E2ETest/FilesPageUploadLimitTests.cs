using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// That the Files page refuses an oversized upload while it is arriving, rather than after.
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven against a real host because the property is an ordering, and ordering cannot be read
/// off the source.</b> Razor Pages validates antiforgery in an authorization filter, and validating
/// reads the form - so a limit applied any later than that is applied after the buffering it was
/// meant to bound, and the page would still pass every assertion about accepting and rejecting
/// files. What distinguishes the two is only ever visible end to end.
/// </para>
/// <para>
/// The cap is turned down through configuration rather than the body turned up, so the test costs
/// kilobytes instead of the 512 MiB the shipped default would need.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class FilesPageUploadLimitTests : IAsyncLifetime, IDisposable
{
    private const int CapBytes = 4096;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-uploadcap-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");
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
    }

    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Fact]
    public async Task AFileUnderTheCapIsStillAccepted()
    {
        // The half that proves the refusal below is the cap doing its job rather than the page being
        // broken - a limit that refuses everything would pass the other test on its own.
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "undercap@example.com");

        string page = await GetPageAsync(client);

        using HttpResponseMessage response = await PostAsync(client, page, "small.gcode", 512);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        client.Dispose();
    }

    [Fact]
    public async Task AFileOverTheCapIsRefusedWhileItArrives()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "overcap@example.com");

        string page = await GetPageAsync(client);

        // Well past the cap plus the form overhead, so it is the body that trips it.
        using HttpResponseMessage response = await PostAsync(client, page, "huge.gcode", CapBytes * 64);

        // Deliberately a range rather than one code. Which of the two bounds trips first depends on
        // the server: TestServer has no request-body-size feature, so the multipart limit refuses it
        // as 400, where Kestrel's own ceiling answers 413. Pinning either would pass here and be
        // wrong about production, and what matters to this test is the same for both.
        response.StatusCode.Should().NotBe(HttpStatusCode.Redirect,
                                           "a refused upload must not look like a stored one");
        ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(400);

        client.Dispose();
    }

    private static async Task<string> GetPageAsync(HttpClient client)
    {
        using HttpResponseMessage page = await client.GetAsync("/Files", TestContext.Current.CancellationToken);

        return await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification =
                         "MultipartFormDataContent takes ownership of the parts added to it and disposes them with itself, which the using declaration below does.")]
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string page, string name, int bytes)
    {
        using MultipartFormDataContent form = [];

        form.Add(new StringContent(AntiforgeryTestHelper.ExtractToken(page)), "__RequestVerificationToken");
        form.Add(new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('G', bytes)))), "file", name);

        return await client.PostAsync("/Files?handler=Upload", form);
    }
}
