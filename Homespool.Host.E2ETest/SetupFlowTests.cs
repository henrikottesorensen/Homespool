using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Drives the real <c>/setup</c> page over HTTP - antiforgery token and all - rather than calling
/// <c>SetupModel.OnPostAsync</c> directly or bypassing it via <see cref="SetupState.MarkComplete"/>
/// the way <see cref="EndToEndEnrolmentTests"/> does. This is the "heavier tier" AGENT-NOTES
/// phase-1.5 §15 flagged since step 4 as needing an integration harness the project didn't have yet
/// - "deferred, still curl-verified only" - now that <c>Microsoft.AspNetCore.Mvc.Testing</c> exists
/// in this project, closing that gap.
/// </summary>
/// <remarks>
/// The one-time bootstrap token is, by design, never exposed anywhere except a single log line at
/// startup (<see cref="AdminBootstrap"/>'s remarks - it is held in memory only and never persisted).
/// So a test that wants to submit it has exactly one legitimate way to get it: read the log, the same
/// way an operator would. <see cref="CapturingSink"/> attaches alongside <c>Program</c>'s own console
/// sink for exactly this, rather than adding a test-only getter to <see cref="SetupState"/> that would
/// weaken the "exposed exactly once" property the class documents.
/// </remarks>
// See EndToEndEnrolmentTests' [Collection] remarks: every WebApplicationFactory-hosted test class
// needs the same collection name so xUnit never races their concurrent Program.Main startups against
// Serilog's shared static Log.Logger.
[Collection("WebApplicationFactory")]
public sealed class SetupFlowTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-setup-{Guid.NewGuid():N}.db");
    private readonly CapturingSink _logs = new();
    private HomespoolFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}", extraSinks: [_logs]);

        // Forces the host to actually start - migrations and AdminBootstrap run at that point, which
        // is what mints and logs the bootstrap token this suite needs. Deliberately does *not* call
        // SetupState.MarkComplete: unlike EndToEndEnrolmentTests, an incomplete setup is the
        // precondition these tests exercise.
        _ = _factory.Server;

        return Task.CompletedTask;
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
    /// The one-time token <c>AdminBootstrap</c> logged at startup - read back the same way an
    /// operator reading the console would, never from any in-memory shortcut.
    /// </summary>
    private string ReadBootstrapTokenFromLog()
    {
        string? token = _logs.FindPropertyValue("SetupToken");
        token.Should().NotBeNullOrWhiteSpace("AdminBootstrap must have logged a token for a database with no administrator");

        return token!;
    }

    /// <summary>
    /// The full happy path: GET for the antiforgery token, POST the logged bootstrap token with
    /// credentials, and the admin account is genuinely created - the gate closes, a second GET 404s,
    /// and the account holds the Admin role with a default team, exactly as a live click-through
    /// verified once in step 4 (AGENT-NOTES phase-1.5 §15).
    /// </summary>
    [Fact]
    public async Task PostingTheLoggedBootstrapTokenCreatesTheAdministratorAndClosesSetup()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        string token = ReadBootstrapTokenFromLog();

        HttpResponseMessage getResponse = await client.GetAsync("/setup");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string antiforgeryToken = AntiforgeryTestHelper.ExtractToken(await getResponse.Content.ReadAsStringAsync());

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgeryToken,
            ["Input.Email"] = "admin@example.com",
            ["Input.Password"] = "Correct-Horse-Battery-Staple-1!",
            ["Input.ConfirmPassword"] = "Correct-Horse-Battery-Staple-1!",
            ["Input.Token"] = token,
        });

        // Act
        HttpResponseMessage postResponse = await client.PostAsync("/setup", body);

        // Assert
        postResponse.StatusCode.Should().Be(HttpStatusCode.Redirect, "a successful setup redirects to the app root");
        postResponse.Headers.Location!.OriginalString.Should().Be("/");
        postResponse.Headers.Should().Contain(h => h.Key == "Set-Cookie", "signing the new admin in issues a cookie");

        HttpResponseMessage secondGet = await client.GetAsync("/setup");
        secondGet.StatusCode.Should().Be(HttpStatusCode.NotFound, "setup must close the moment an administrator exists");

        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
        IList<HSUser> admins = await userManager.GetUsersInRoleAsync(AdminBootstrap.AdminRole);

        admins.Should().ContainSingle().Which.Email.Should().Be("admin@example.com");
    }

    /// <summary>A wrong token is rejected, and setup stays open for a correct attempt afterwards.</summary>
    [Fact]
    public async Task PostingTheWrongTokenIsRejectedAndSetupStaysOpen()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage getResponse = await client.GetAsync("/setup");
        string antiforgeryToken = AntiforgeryTestHelper.ExtractToken(await getResponse.Content.ReadAsStringAsync());

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgeryToken,
            ["Input.Email"] = "admin@example.com",
            ["Input.Password"] = "Correct-Horse-Battery-Staple-1!",
            ["Input.ConfirmPassword"] = "Correct-Horse-Battery-Staple-1!",
            ["Input.Token"] = "not-the-real-token",
        });

        // Act
        HttpResponseMessage postResponse = await client.PostAsync("/setup", body);

        // Assert
        postResponse.StatusCode.Should().Be(HttpStatusCode.OK, "a rejected token re-renders the page rather than redirecting");

        HttpResponseMessage stillOpenGet = await client.GetAsync("/setup");
        stillOpenGet.StatusCode.Should().Be(HttpStatusCode.OK, "no administrator was created, so setup must still be reachable");
    }

    /// <summary>
    /// A request missing the antiforgery token entirely is rejected before the handler even runs -
    /// proving the protection is real, not merely present in the markup.
    /// </summary>
    [Fact]
    public async Task PostingWithoutTheAntiforgeryTokenIsRejected()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // A GET first, so the antiforgery cookie is present - only the form field is withheld, which
        // is exactly what a forged cross-site request would look like.
        await client.GetAsync("/setup");
        string token = ReadBootstrapTokenFromLog();

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["Input.Email"] = "admin@example.com",
            ["Input.Password"] = "Correct-Horse-Battery-Staple-1!",
            ["Input.ConfirmPassword"] = "Correct-Horse-Battery-Staple-1!",
            ["Input.Token"] = token,
        });

        // Act
        HttpResponseMessage postResponse = await client.PostAsync("/setup", body);

        // Assert
        postResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        HttpResponseMessage stillOpenGet = await client.GetAsync("/setup");
        stillOpenGet.StatusCode.Should().Be(HttpStatusCode.OK, "the rejected request must not have created an administrator");
    }
}
