using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Homespool.Host.Accounts;
using Homespool.Host.Configuration;
using Homespool.Host.Mail;
using Homespool.Host.Middleware;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The settings page against a real host: who may reach it, that a save reaches the options a
/// consumer reads, and that the secret survives an unrelated edit.
/// </summary>
/// <remarks>
/// <b>The point of doing this end to end</b> is the chain no unit test spans: a form post reaches the
/// store, the store writes a file and reloads configuration, and an <c>IOptionsMonitor</c> resolved
/// afterwards sees the new value. Every link of that is somebody else's code, and the whole grading
/// scheme rests on it holding.
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class SettingsPageTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-settings-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

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

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task AnOrdinaryAccountCannotReachIt()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "ordinary-settings@example.com");

        using HttpResponseMessage response = await client.GetAsync("/Admin/Settings", TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);

        client.Dispose();
    }

    /// <summary>
    /// A page that saves and changes nothing is worse than no page, so when a change lands is always
    /// on the page - stated once for the ordinary case, and marked on the exceptions.
    /// </summary>
    /// <remarks>
    /// <b>The ordinary case is deliberately not repeated per field.</b> Saying "applies immediately"
    /// on two dozen rows buried the two that do not, which is the opposite of what the badge was for.
    /// </remarks>
    [Fact]
    public async Task WhenAChangeLandsIsAlwaysOnThePage()
    {
        HttpClient admin = await AdminAsync("settings-grades@example.com");

        string page = await admin.GetStringAsync("/Admin/Settings", TestContext.Current.CancellationToken);

        page.Should().Contain("apply immediately unless a field says otherwise",
                              "the ordinary case is stated once rather than on every row");
        page.Should().Contain("Applies after a restart");
        page.Should().Contain("Applies on the next sweep, within an hour");
        page.Should().NotContain("Applies immediately",
                                 "repeating it per row is what buried the exceptions");

        admin.Dispose();
    }

    /// <summary>
    /// The whole chain: a post becomes a file, a reload, and a value a consumer resolves afterwards.
    /// </summary>
    [Fact]
    public async Task ASavedValueReachesTheOptionsAConsumerReads()
    {
        HttpClient admin = await AdminAsync("settings-live@example.com");

        using HttpResponseMessage response = await PostAsync(admin, new Dictionary<string, string>
        {
            ["Values[Invitations:LifetimeHours]"] = "72",
        });

        response.IsSuccessStatusCode.Should().BeTrue();

        using IServiceScope scope = _factory.Services.CreateScope();

        scope.ServiceProvider
             .GetRequiredService<IOptionsMonitor<InvitationOptions>>()
             .CurrentValue
             .LifetimeHours
             .Should()
             .Be(72, "the save reloaded configuration, and the monitor is reading it");

        admin.Dispose();
    }

    [Fact]
    public async Task AValueOutsideItsRangeIsRefusedAndSaysSo()
    {
        HttpClient admin = await AdminAsync("settings-invalid@example.com");

        using HttpResponseMessage response = await PostAsync(admin, new Dictionary<string, string>
        {
            ["Values[Smtp:Port]"] = "70000",
        });

        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.Should().Contain("Nothing was saved");

        using IServiceScope scope = _factory.Services.CreateScope();

        scope.ServiceProvider
             .GetRequiredService<IOptionsMonitor<SmtpOptions>>()
             .CurrentValue
             .Port
             .Should()
             .Be(587, "a refused save changes nothing");

        admin.Dispose();
    }

    /// <summary>
    /// The form posts back what it was shown, so the mask must not be able to destroy the password
    /// it stands for — the defect a camera credential already produced once.
    /// </summary>
    [Fact]
    public async Task EditingAnotherFieldDoesNotDestroyTheStoredPassword()
    {
        HttpClient admin = await AdminAsync("settings-secret@example.com");

        // Naming a mail server is asked about, so this agrees on the way past; what the test is
        // about is the password surviving the edit that follows.
        using (HttpResponseMessage first = await PostAsync(admin, new Dictionary<string, string>
        {
            ["Values[Smtp:Host]"] = "mail.example.com",
            ["Values[Smtp:Password]"] = "hunter2",
            ["Confirmed"] = "Smtp:Host",
        }))
        {
            first.IsSuccessStatusCode.Should().BeTrue();
        }

        string page = await admin.GetStringAsync("/Admin/Settings", TestContext.Current.CancellationToken);

        page.Should().NotContain("hunter2", "a stored secret is never rendered back");

        using (HttpResponseMessage second = await PostAsync(admin, new Dictionary<string, string>
        {
            ["Values[Smtp:Host]"] = "other.example.com",
            ["Values[Smtp:Password]"] = SettingsStore.SecretPlaceholder,
        }))
        {
            second.IsSuccessStatusCode.Should().BeTrue();
        }

        using IServiceScope scope = _factory.Services.CreateScope();

        SmtpOptions smtp = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<SmtpOptions>>().CurrentValue;

        smtp.Host.Should().Be("other.example.com", "the edited field changed");
        smtp.Password.Should().Be("hunter2", "and the password nobody was shown survived it");

        admin.Dispose();
    }

    /// <summary>
    /// An administrator with no authenticator of their own cannot turn the requirement on: it applies
    /// to accounts rather than sessions, so doing it would send them to enrol and leave them unable to
    /// reach the page that would undo it.
    /// </summary>
    [Fact]
    public async Task EnablingTwoFactorWithoutOneOfYourOwnIsRefused()
    {
        HttpClient admin = await AdminAsync("settings-refuse@example.com");

        using HttpResponseMessage response = await PostAsync(admin, new Dictionary<string, string>
        {
            ["Values[Security:RequireTwoFactor]"] = "true",
        });

        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.Should().Contain("Set up an authenticator on your own account first");

        using IServiceScope scope = _factory.Services.CreateScope();

        scope.ServiceProvider
             .GetRequiredService<IOptionsMonitor<SecurityOptions>>()
             .CurrentValue
             .RequireTwoFactor
             .Should()
             .BeFalse();

        admin.Dispose();
    }

    // Being asked, agreeing, and turning it back off are covered in SettingsModelTests rather than
    // here. An administrator created by the enrolment helper has no authenticator, so this page can
    // only ever reach the refusal above - and once the requirement is on, the account that turned it
    // on is redirected to enrol before it can fetch any page at all.

    // Turning it back off is not asserted here, and cannot be: enabling the requirement locks the
    // enabling administrator out of every page until they enrol an authenticator, which is what
    // SecurityOptions describes as "the first administrator meets it immediately". The page cannot be
    // fetched afterwards, so the off direction is covered in SettingsModelTests instead.
    private async Task<HttpClient> AdminAsync(string email)
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, email, AdminBootstrap.AdminRole);

        return client;
    }

    private async Task<HttpResponseMessage> PostAsync(HttpClient client, Dictionary<string, string> values)
    {
        string page = await client.GetStringAsync("/Admin/Settings", TestContext.Current.CancellationToken);

        Dictionary<string, string> form = new(values, StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page),
        };

        using FormUrlEncodedContent content = new(form);

        return await client.PostAsync("/Admin/Settings", content, TestContext.Current.CancellationToken);
    }
}
