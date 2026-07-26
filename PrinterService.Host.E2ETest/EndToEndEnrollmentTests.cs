using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using PrinterService.Host.Services;
using PrinterService.Model.Entities;

namespace PrinterService.Host.E2ETest;

/// <summary>
/// The full enrollment loop through the real ASP.NET Core pipeline - routing, the setup gate,
/// cookie authentication, <c>[Authorize]</c>, and every controller and service in between - rather
/// than calling services directly the way the rest of this project's tests do. This is what
/// AGENT-NOTES phase-1.5 §15 had flagged, since step 4, as "not verified: no live walk-through",
/// turned into something that runs on every test pass instead of a one-off manual click-through.
/// </summary>
/// <remarks>
/// Each test spins up its own <see cref="WebApplicationFactory{TEntryPoint}"/> against a fresh
/// temp-file SQLite database - the same real-SQLite convention every other phase-1.5 test in this
/// project follows, for the same reason: <c>PSDbContext</c>'s <c>DateTimeOffset</c> comparisons only
/// translate against the real provider.
/// </remarks>
// Program.Main reassigns Serilog's process-wide static Log.Logger at startup, so two
// WebApplicationFactory-hosted test classes starting concurrently race on it - one host's logger
// configuration can silently clobber another's mid-startup. [Collection] groups every such class
// under one name so xUnit runs them sequentially against each other rather than in parallel; other
// collections are unaffected.
[Collection("WebApplicationFactory")]
public sealed class EndToEndEnrollmentTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-e2e-{Guid.NewGuid():N}.db");
    private PrinterServiceFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new PrinterServiceFactory($"Data Source={_databasePath}");

        // Force the host to actually start (migrations + AdminBootstrap run at that point) before any
        // test touches it, rather than lazily on the first HttpClient call.
        _ = _factory.Server;

        // No /setup walk-through here: SetupState.MarkComplete is the exact call Setup.cshtml.cs makes
        // on success, and driving the real page would mean fighting Razor Pages' automatic antiforgery
        // validation to test something SetupStateTests and SetupGateMiddlewareTests already cover. What
        // this suite exists to verify is the enrollment loop past that point.
        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();
    }

    public Task DisposeAsync()
    {
        Dispose();

        return Task.CompletedTask;
    }

    // CA1001 wants IDisposable on a type owning a disposable field even though xUnit's IAsyncLifetime
    // already drives cleanup via DisposeAsync above; WebApplicationFactory.Dispose is idempotent, so
    // this is a safe, redundant satisfier rather than a second real teardown path.
    public void Dispose()
    {
        _factory.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// The whole loop, in order: a printer registers itself and receives a code; before anyone
    /// claims it, polling reports "not yet"; a signed-in user claims it through the app API; the
    /// printer's next poll now returns a real token; and that same user can list, read and patch the
    /// printer it just claimed through the app API.
    /// </summary>
    [Fact]
    public async Task PrinterRegistersIsClaimedAndTheFullAppApiLoopWorksThroughRealHttp()
    {
        using HttpClient anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // ---------- printer: POST /p/register ----------
        HttpResponseMessage registerResponse = await EnrollmentFlowHelper.SendPrinterRegisterAsync(anonymous, new
        {
            sn = "E2E-SERIAL-0001",
            fingerprint = "E2E-FINGERPRINT-0001",
            printer_type = "1.3.5",
            firmware = "6.4.0+11974",
        });

        registerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string code = registerResponse.Headers.GetValues("Code").Single();
        code.Should().NotBeNullOrWhiteSpace();

        // ---------- printer: GET /p/register before anyone has claimed it ----------
        HttpResponseMessage prePollResponse = await EnrollmentFlowHelper.SendPollAsync(anonymous, code);
        prePollResponse.StatusCode.Should().Be(HttpStatusCode.Accepted, "nobody has claimed the printer yet");

        // ---------- app: a signed-in user claims it ----------
        (PSUser claimer, HttpClient appClient) = await EnrollmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "claimer@example.com");
        using (appClient)
        {
            HttpResponseMessage claimResponse = await appClient.PostAsJsonAsync("/api/v1/printers/register", new
            {
                name = "Living room MK4",
                location = "Living room",
                code,
            });

            claimResponse.StatusCode.Should().Be(HttpStatusCode.Created);

            JsonDocument claimed = JsonDocument.Parse(await claimResponse.Content.ReadAsStringAsync());
            Guid uuid = claimed.RootElement.GetProperty("uuid").GetGuid();
            claimed.RootElement.GetProperty("name").GetString().Should().Be("Living room MK4");
            claimed.RootElement.GetProperty("state").GetString().Should().Be("UNKNOWN");

            // ---------- printer: GET /p/register now that it has been claimed ----------
            HttpResponseMessage postPollResponse = await EnrollmentFlowHelper.SendPollAsync(anonymous, code);
            postPollResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            postPollResponse.Headers.GetValues("Token").Single().Should().NotBeNullOrWhiteSpace();

            // ---------- app: GET /api/v1/user ----------
            JsonDocument user = JsonDocument.Parse(await (await appClient.GetAsync("/api/v1/user")).Content.ReadAsStringAsync());
            user.RootElement.GetProperty("id").GetInt64().Should().Be(claimer.Id);
            user.RootElement.GetProperty("teams").GetArrayLength().Should().Be(1, "every account has exactly one default team");

            // ---------- app: GET /api/v1/printers ----------
            HttpResponseMessage listResponse = await appClient.GetAsync("/api/v1/printers");
            listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonDocument list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
            list.RootElement.GetArrayLength().Should().Be(1);
            list.RootElement[0].GetProperty("uuid").GetGuid().Should().Be(uuid);

            // ---------- app: GET /api/v1/printers/{uuid} ----------
            HttpResponseMessage getResponse = await appClient.GetAsync($"/api/v1/printers/{uuid}");
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            // ---------- app: PATCH /api/v1/printers/{uuid} ----------
            using JsonContent patchBody = JsonContent.Create(new { name = "Renamed MK4", location = "Garage" });
            HttpResponseMessage patchResponse = await appClient.PatchAsync($"/api/v1/printers/{uuid}", patchBody);

            patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonDocument patched = JsonDocument.Parse(await patchResponse.Content.ReadAsStringAsync());
            patched.RootElement.GetProperty("name").GetString().Should().Be("Renamed MK4");
            patched.RootElement.GetProperty("location").GetString().Should().Be("Garage");

            HttpResponseMessage reGetResponse = await appClient.GetAsync($"/api/v1/printers/{uuid}");
            JsonDocument reGet = JsonDocument.Parse(await reGetResponse.Content.ReadAsStringAsync());
            reGet.RootElement.GetProperty("name").GetString().Should().Be("Renamed MK4", "the patch must have persisted");
        }
    }

    /// <summary>
    /// The app API genuinely requires cookie authentication - an anonymous request is challenged
    /// (redirected towards Login) rather than served, proving <c>[Authorize]</c> is wired through the
    /// real pipeline rather than only asserted at the unit level.
    /// </summary>
    [Fact]
    public async Task AppApiEndpointsChallengeAnAnonymousCaller()
    {
        using HttpClient anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await anonymous.GetAsync("/api/v1/printers");

        // 401, not a 302: [ApiController] makes ASP.NET Core's cookie-auth challenge respond with a
        // status code instead of an HTML redirect. Confirmed by comparison against a plain (non
        // [ApiController]) [Authorize] Razor Page, which redirects (302) for the identical challenge -
        // both compute the same Location, so this is App API vs Pages, not a missing redirect.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
    }

    /// <summary>
    /// Before an administrator exists, <c>SetupGateMiddleware</c> redirects everything to
    /// <c>/setup</c> - including the app API, which isn't in its explicit allow-list alongside
    /// <c>/p</c>. Nobody can hold an account at that point anyway (an admin has to exist to invite
    /// anyone), so this is a consequence of the gate rather than an independent decision, but it's
    /// worth pinning down given how easy it would be for a future allow-list edit to change it
    /// silently.
    /// </summary>
    [Fact]
    public async Task AppApiIsBlockedBySetupGateBeforeAnAdministratorExists()
    {
        // Arrange - a second, not-yet-set-up instance; the shared _factory already completed setup
        // in InitializeAsync, so this one deliberately doesn't call MarkComplete.
        string databasePath = Path.Combine(Path.GetTempPath(), $"ps-e2e-presetup-{Guid.NewGuid():N}.db");

        await using PrinterServiceFactory presetupFactory = new($"Data Source={databasePath}");
        using HttpClient client = presetupFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        try
        {
            // Act
            HttpResponseMessage response = await client.GetAsync("/api/v1/printers");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            response.Headers.Location!.OriginalString.Should().Contain("/setup");
        }
        finally
        {
            foreach (string path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
