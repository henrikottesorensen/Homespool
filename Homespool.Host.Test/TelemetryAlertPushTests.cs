using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Health;
using Homespool.Host.Mail;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Health alerts reaching administrators' browsers: who is pushed to, in what words, that an incident
/// is pushed once and its all-clear replaces it, and that a database gone missing does not stop it.
/// </summary>
/// <remarks>
/// The email half has its own test against a real mail server, in the integration project; here the
/// sender only records, so a test can say whether mail was attempted at all.
/// </remarks>
public sealed class TelemetryAlertPushTests : IAsyncLifetime
{
    private const string Problem = "Nothing is reaching the database.";

    private readonly string _databasePath = WebPushRig.NewDatabasePath();
    private readonly CapturingEmailSender _mail = new();
    private readonly FakeLogger<TelemetryAlertService> _logger = new();
    private readonly List<FakePushBrowser> _browsers = [];

    private HealthStatus _status = HealthStatus.Healthy;
    private string _connectionString;
    private WebPushRig _rig = null!;

    public TelemetryAlertPushTests()
    {
        _connectionString = $"Data Source={_databasePath}";
    }

    public async ValueTask InitializeAsync()
    {
        _rig = await WebPushRig.CreateAsync(_databasePath, new EphemeralDataProtectionProvider(), services: services =>
        {
            // Read when each scope is made, so a test can take the database away between two polls.
            services.AddDbContext<HomespoolDbContext>((_, options) => options.UseSqlite(_connectionString));

            services.AddScoped<IEmailSender>(_ => _mail);
            services.AddHealthChecks()
                    .AddCheck("telemetry-persistence", () => new HealthCheckResult(_status, Problem));
        });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (FakePushBrowser browser in _browsers)
        {
            browser.Dispose();
        }

        await _rig.DisposeAsync();
        WebPushRig.Delete(_databasePath);
    }

    /// <summary>
    /// Every open administrator's browsers, each in its owner's language, and nobody else's: not a
    /// member's, not a closed administrator's, and not one whose owner turned health notifications
    /// off. What it says is the check's own description, and tapping it opens the front page.
    /// </summary>
    [Fact]
    public async Task AnAlertIsPushedToEveryAdministratorsBrowserInTheirOwnLanguage()
    {
        // Arrange
        FakePushBrowser dane = await AddAsync("dane@example.com", administrator: true, language: "da");
        FakePushBrowser brit = await AddAsync("brit@example.com", administrator: true);
        FakePushBrowser member = await AddAsync("member@example.com", administrator: false);
        FakePushBrowser closed = await AddAsync("closed@example.com", administrator: true, closed: true);
        FakePushBrowser muted = await AddAsync("muted@example.com", administrator: true, muted: "ServiceHealth");
        using TelemetryAlertService alerts = NewService();
        _status = HealthStatus.Unhealthy;

        // Act
        await alerts.PollAsync(TestContext.Current.CancellationToken);

        // Assert
        _rig.PushService.Received.Select(push => push.Endpoint.ToString())
            .Should().BeEquivalentTo([dane.Endpoint, brit.Endpoint]);
        _rig.PushService.Received.Select(push => push.Endpoint.ToString())
            .Should().NotContain([member.Endpoint, closed.Endpoint, muted.Endpoint]);

        JsonElement danish = PayloadFor(dane);
        danish.GetProperty("title").GetString().Should().Be("Homespool har et problem");
        danish.GetProperty("body").GetString().Should().Be(Problem, "the check's own description is not ours to translate");
        danish.GetProperty("url").GetString().Should().Be("/");
        danish.GetProperty("tag").GetString().Should().Be(TelemetryAlertService.HealthTag);

        PayloadFor(brit).GetProperty("title").GetString().Should().Be("Homespool is unhealthy");
        _rig.PushService.Received.Should().OnlyContain(push => push.Header("Urgency") == "high");
    }

    /// <summary>
    /// One push for the incident however many polls it lasts, and one for the all-clear - with the
    /// alert's tag, so it replaces the alert rather than sitting beside it.
    /// </summary>
    [Fact]
    public async Task AnIncidentIsPushedOnceAndItsAllClearReplacesIt()
    {
        // Arrange
        FakePushBrowser admin = await AddAsync("admin@example.com", administrator: true);
        using TelemetryAlertService alerts = NewService();

        // Act
        _status = HealthStatus.Unhealthy;
        await alerts.PollAsync(TestContext.Current.CancellationToken);
        await alerts.PollAsync(TestContext.Current.CancellationToken);
        int duringIncident = _rig.PushService.Received.Count;

        _status = HealthStatus.Healthy;
        await alerts.PollAsync(TestContext.Current.CancellationToken);
        await alerts.PollAsync(TestContext.Current.CancellationToken);

        // Assert
        duringIncident.Should().Be(1, "a push alone reports the incident, without mail configured");
        _rig.PushService.Received.Should().HaveCount(2);

        FakePush allClear = _rig.PushService.Received[1];
        JsonElement payload = admin.DecryptJson(allClear.Body);
        payload.GetProperty("title").GetString().Should().Be("Homespool has recovered");
        payload.GetProperty("tag").GetString().Should().Be(TelemetryAlertService.HealthTag);
        allClear.Header("Urgency").Should().BeNull("normal is the default, which goes unsaid - not the alert's high");
    }

    /// <summary>
    /// A push the service did not take is not a reported incident: the next poll tries again. And the
    /// failure is recorded on the browser's row, as any other delivery's is.
    /// </summary>
    [Fact]
    public async Task AnAlertNoBrowserTookIsTriedAgainAndTheFailureRecorded()
    {
        // Arrange
        await AddAsync("admin@example.com", administrator: true);
        using TelemetryAlertService alerts = NewService();
        _status = HealthStatus.Unhealthy;
        _rig.PushService.Answer = HttpStatusCode.ServiceUnavailable;

        // Act
        await alerts.PollAsync(TestContext.Current.CancellationToken);
        WebPushDestination afterFailure = await StoredBrowserAsync();

        _rig.PushService.Answer = HttpStatusCode.Created;
        await alerts.PollAsync(TestContext.Current.CancellationToken);
        WebPushDestination afterRetry = await StoredBrowserAsync();

        // Assert
        _rig.PushService.Received.Should().HaveCount(2);
        afterFailure.ConsecutiveFailures.Should().Be(1);
        afterRetry.ConsecutiveFailures.Should().Be(0);
        afterRetry.LastDeliveredAt.Should().NotBeNull();
    }

    /// <summary>
    /// The alert the list is kept for. The database goes away after a poll that worked; the next
    /// poll's alert still reaches the browser read before, signed with the key read before, and failing
    /// to record how it went is a warning rather than the end of the poll.
    /// </summary>
    [Fact]
    public async Task AnOutageIsPushedToTheBrowsersReadBeforeIt()
    {
        // Arrange
        FakePushBrowser admin = await AddAsync("admin@example.com", administrator: true);
        using TelemetryAlertService alerts = NewService();
        await alerts.PollAsync(TestContext.Current.CancellationToken);

        // Act
        _connectionString = $"Data Source={System.IO.Path.Combine(_databasePath + ".missing", "none.db")};Mode=ReadOnly";
        _status = HealthStatus.Unhealthy;
        await alerts.PollAsync(TestContext.Current.CancellationToken);

        // Assert
        _rig.PushService.Received.Should().ContainSingle().Which.Endpoint.ToString().Should().Be(admin.Endpoint);
        _logger.Collector.GetSnapshot()
               .Should().Contain(record => record.Level == LogLevel.Warning && record.Message.StartsWith("Could not record", StringComparison.Ordinal));
        _logger.Collector.GetSnapshot().Should().NotContain(record => record.Level == LogLevel.Error);
    }

    /// <summary>
    /// Mail is tried only where a mail server is configured, and a deployment with one tells the
    /// administrator both ways.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MailIsTriedOnlyWithAMailServer(bool mailConfigured)
    {
        // Arrange
        await AddAsync("admin@example.com", administrator: true);
        using TelemetryAlertService alerts = NewService(mailConfigured);
        _status = HealthStatus.Unhealthy;

        // Act
        await alerts.PollAsync(TestContext.Current.CancellationToken);

        // Assert
        _rig.PushService.Received.Should().ContainSingle();
        _mail.SentEmails.Select(mail => mail.email).Should().Equal(mailConfigured ? ["admin@example.com"] : []);
    }

    /// <summary>
    /// An unhealthy deployment with nobody to tell says so once, not every minute - and again for the
    /// next incident, once the first has cleared.
    /// </summary>
    [Fact]
    public async Task NobodyToTellIsLoggedOncePerIncident()
    {
        // Arrange - an administrator with an address and no mail server to send through.
        await AddUserAsync("admin@example.com", administrator: true);
        using TelemetryAlertService alerts = NewService();

        // Act
        _status = HealthStatus.Unhealthy;
        await alerts.PollAsync(TestContext.Current.CancellationToken);
        await alerts.PollAsync(TestContext.Current.CancellationToken);
        await alerts.PollAsync(TestContext.Current.CancellationToken);
        int duringFirst = Nobodies();

        _status = HealthStatus.Healthy;
        await alerts.PollAsync(TestContext.Current.CancellationToken);
        _status = HealthStatus.Unhealthy;
        await alerts.PollAsync(TestContext.Current.CancellationToken);

        // Assert
        duringFirst.Should().Be(1);
        Nobodies().Should().Be(2);
        _mail.SentEmails.Should().BeEmpty();

        int Nobodies()
        {
            return _logger.Collector.GetSnapshot()
                          .Count(record => record.Level == LogLevel.Warning && record.Message.Contains("no administrator has", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(TelemetryAlertService.MaxPushBodyLength, TelemetryAlertService.MaxPushBodyLength)]
    [InlineData(TelemetryAlertService.MaxPushBodyLength + 1, TelemetryAlertService.MaxPushBodyLength)]
    public void APushCarriesTheProblemsCutToFit(int descriptionLength, int expectedLength)
    {
        // Arrange
        HealthReport report = new(
            new Dictionary<string, HealthReportEntry>
            {
                ["broken"] = new(HealthStatus.Unhealthy, new string('x', descriptionLength), TimeSpan.Zero, null, null),
                ["fine"] = new(HealthStatus.Healthy, "All good.", TimeSpan.Zero, null, null),
            },
            TimeSpan.Zero);

        // Act
        string body = TelemetryAlertService.DescribeForPush(report);

        // Assert
        body.Length.Should().Be(expectedLength);
        body.Should().NotContain("All good.");
        body.EndsWith('…').Should().Be(descriptionLength > TelemetryAlertService.MaxPushBodyLength);
    }

    /// <summary>
    /// The mail encodes a description because it builds markup from it; a notification's body is
    /// shown as text, where an entity would be shown as one.
    /// </summary>
    [Fact]
    public void APushCarriesADescriptionAsText()
    {
        // Arrange
        HealthReport report = new(
            new Dictionary<string, HealthReportEntry>
            {
                ["broken"] = new(HealthStatus.Unhealthy, "Refused <b>everything</b> & gave up", TimeSpan.Zero, null, null),
            },
            TimeSpan.Zero);

        // Act
        string body = TelemetryAlertService.DescribeForPush(report);

        // Assert
        body.Should().Be("Refused <b>everything</b> & gave up");
    }

    [Fact]
    public void APushNeverEndsHalfWayThroughACharacter()
    {
        // Arrange - an emoji, two UTF-16 units, straddling the cut.
        string description = new string('x', TelemetryAlertService.MaxPushBodyLength - 2) + "😀" + "tail";
        HealthReport report = new(
            new Dictionary<string, HealthReportEntry>
            {
                ["broken"] = new(HealthStatus.Unhealthy, description, TimeSpan.Zero, null, null),
            },
            TimeSpan.Zero);

        // Act
        string body = TelemetryAlertService.DescribeForPush(report);

        // Assert
        body.Should().Be(new string('x', TelemetryAlertService.MaxPushBodyLength - 2) + "…");
    }

    private TelemetryAlertService NewService(bool mailConfigured = false)
    {
        return new TelemetryAlertService(_rig.Services.GetRequiredService<HealthCheckService>(),
                                         _rig.Services.GetRequiredService<IServiceScopeFactory>(),
                                         Options.Create(new SmtpOptions { Host = mailConfigured ? "smtp.example.com" : string.Empty }),
                                         _logger);
    }

    private JsonElement PayloadFor(FakePushBrowser browser)
    {
        return browser.DecryptJson(_rig.PushService.Received.Single(push => push.Endpoint.ToString() == browser.Endpoint).Body);
    }

    private Task<WebPushDestination> StoredBrowserAsync()
    {
        return _rig.InScopeAsync(services => services.GetRequiredService<HomespoolDbContext>()
                                                     .WebPushDestinations.AsNoTracking()
                                                     .SingleAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>An account with one browser.</summary>
    private async Task<FakePushBrowser> AddAsync(string email,
                                                 bool administrator,
                                                 string? language = null,
                                                 bool closed = false,
                                                 string? muted = null)
    {
        HSUser user = await AddUserAsync(email, administrator, language, closed, muted);

        FakePushBrowser browser = FakePushService.NewBrowser();
        _browsers.Add(browser);
        await _rig.AddBrowserAsync(user.Id, browser);

        return browser;
    }

    private async Task<HSUser> AddUserAsync(string email,
                                            bool administrator,
                                            string? language = null,
                                            bool closed = false,
                                            string? muted = null)
    {
        HSUser user = await _rig.AddUserAsync(email, language);

        await _rig.InScopeAsync(async services =>
        {
            HomespoolDbContext db = services.GetRequiredService<HomespoolDbContext>();

            await db.Users.Where(row => row.Id == user.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.DeactivatedAt, closed ? DateTimeOffset.UtcNow : null)
                                                          .SetProperty(row => row.MutedNotifications, muted),
                                        TestContext.Current.CancellationToken);

            if (administrator)
            {
                IdentityRole<long> role = await db.Roles.SingleOrDefaultAsync(r => r.Name == AdminBootstrap.AdminRole, TestContext.Current.CancellationToken) ??
                                          db.Roles.Add(new IdentityRole<long>(AdminBootstrap.AdminRole) { NormalizedName = "ADMIN" }).Entity;
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);

                db.UserRoles.Add(new IdentityUserRole<long> { UserId = user.Id, RoleId = role.Id });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            return true;
        });

        return user;
    }
}
