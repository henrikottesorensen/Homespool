using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Mail;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// First-run setup on a box that already has SMTP configured: the administrator is confirmed at
/// creation all the same, where every other creation path would leave the account unconfirmed and
/// waiting for mail. Its own fixture, because the SMTP setting must be in place before the host
/// starts.
/// </summary>
/// <remarks>
/// Before 2026-09-06 setup followed the policy, which left the first administrator unconfirmed on a
/// box with SMTP, signed in once and refused at the next sign-in with nothing pointing them to the
/// resend page. The bootstrap token from the console is the administrator's proof of who they are.
/// </remarks>
public sealed class SetupWithMailTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("setup-mail");
    private readonly CapturingSink _logs = new();
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch, extraSinks: [_logs]);
        _factory.ConfigurationOverrides[$"{SmtpOptions.SectionName}:Host"] = "smtp.example.com";

        // Starts the host, which mints and logs the bootstrap token; setup stays open.
        _ = _factory.Server;

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }

    [Fact]
    public async Task TheFirstAdministratorIsConfirmedAtCreationEvenWithMailConfigured()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        string token = _logs.FindPropertyValue("SetupToken")!;
        token.Should().NotBeNullOrWhiteSpace("AdminBootstrap must have logged a token for a database with no administrator");

        HttpResponseMessage getResponse = await client.GetAsync("/setup", TestContext.Current.CancellationToken);
        string antiforgeryToken =
            AntiforgeryTestHelper.ExtractToken(await getResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgeryToken,
            ["Input.Email"] = "admin@example.com",
            ["Input.Username"] = "admin",
            ["Input.Password"] = "Correct-Horse-Battery-Staple-1!",
            ["Input.ConfirmPassword"] = "Correct-Horse-Battery-Staple-1!",
            ["Input.Token"] = token,
        });

        // Act
        HttpResponseMessage postResponse = await client.PostAsync("/setup", body, TestContext.Current.CancellationToken);

        // Assert
        postResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        IdentityCookieTestHelper.SetTheApplicationCookie(_factory.Services, postResponse).Should().BeTrue("setup signs the administrator in");

        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
        HSUser admin = (await userManager.FindByEmailAsync("admin@example.com"))!;

        admin.EmailConfirmed.Should().BeTrue("the bootstrap token is the proof; nobody else can vouch for the first address");
        (await userManager.IsInRoleAsync(admin, AdminBootstrap.AdminRole)).Should().BeTrue();
    }
}
