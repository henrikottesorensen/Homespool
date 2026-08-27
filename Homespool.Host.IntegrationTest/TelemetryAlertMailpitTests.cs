using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Health;
using Homespool.Host.Localisation;
using Homespool.Host.Mail;
using Homespool.Model.Entities;

namespace Homespool.Host.IntegrationTest;

/// <summary>
/// <see cref="TelemetryAlertService"/> against a real SMTP server: an unhealthy service actually
/// puts a message in an administrator's inbox.
/// </summary>
/// <remarks>
/// <para>
/// The last unverified claim from the session that built the alerting. Everything either side of
/// this was covered - <see cref="AlertTransition"/> decides when to send, and the
/// <c>SmtpEmailSender</c> tests in this project prove the transport - but nothing had ever shown
/// the service resolving a recipient from the database and handing a real message to a real
/// server. Those are exactly the joins that a unit test cannot make.
/// </para>
/// <para>
/// Needs Mailpit on localhost:1025, same as its neighbours here.
/// </para>
/// </remarks>
// Serialised, not parallel: all three classes share the one Mailpit container, and each clears the
// mailbox in InitializeAsync. DELETE /api/v1/messages has no filter, so a class starting up wipes
// whatever another class is mid-way through asserting on. Same pattern, and same reason, as
// [Collection("WebApplicationFactory")] in the E2E project.
[Collection("Mailpit")]
public sealed class TelemetryAlertMailpitTests : IAsyncLifetime, IDisposable
{
    private const string AdminAddress = "operator@printerservice.test";

    private readonly MailpitClient _mailpit = new();
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-alert-{Guid.NewGuid():N}.db");
    private ServiceProvider? _provider;

    public ValueTask InitializeAsync()
    {
        return new(_mailpit.ClearAsync());
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _mailpit.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Builds the slice of the host the alert service actually touches: a database with Identity in
    /// it, an SMTP sender pointed at Mailpit, and a health check reporting whatever this test wants.
    /// </summary>
    private async Task<ServiceProvider> BuildAsync(HealthStatus status, string? adminLanguage = null)
    {
        ServiceCollection services = new();

        services.AddLogging();
        services.AddDbContext<HomespoolDbContext>(o => o.UseSqlite($"Data Source={_databasePath}"));

        services.AddIdentityCore<HSUser>()
                .AddRoles<IdentityRole<long>>()
                .AddEntityFrameworkStores<HomespoolDbContext>();

        services.AddSingleton(Options.Create(new SmtpOptions
        {
            Host = "localhost",
            Port = 1025,
            DisableTls = true,
            FromAddress = "no-reply@printerservice.test",
            FromName = "Homespool",
            TimeoutSeconds = 5,
        }));

        services.AddSingleton<ISmtpTransportFactory, MailKitSmtpTransportFactory>();
        services.AddScoped<IEmailSender, SmtpEmailSender>();

        // The alert composes each message in its recipient's own language, so it resolves a
        // localiser and the culture lookup from the scope it sends in. Registered here rather than
        // stubbed: the point of these tests is that a real send works, and a missing registration
        // would be swallowed by the "a failure to report a failure must not take the reporter down"
        // catch and read as silence.
        services.AddLocalization();
        services.AddScoped<UserCultures>();

        // A stand-in for the telemetry check, so this test controls health without needing a broken
        // database - what is under test is the alerting, not the diagnosis.
        services.AddHealthChecks()
                .AddCheck("telemetry-persistence", () => new HealthCheckResult(
                              status, "Nothing is reaching the database."));

        _provider = services.BuildServiceProvider();

        await using (AsyncServiceScope scope = _provider.CreateAsyncScope())
        {
            HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

            RoleManager<IdentityRole<long>> roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<long>>>();
            await roles.CreateAsync(new IdentityRole<long>(AdminBootstrap.AdminRole));

            UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
            HSUser admin = new("operator") { Email = AdminAddress, EmailConfirmed = true, Language = adminLanguage };

            (await users.CreateAsync(admin, "Correct-Horse-Battery-1!")).Succeeded.Should().BeTrue();
            (await users.AddToRoleAsync(admin, AdminBootstrap.AdminRole)).Succeeded.Should().BeTrue();
        }

        return _provider;
    }

    private TelemetryAlertService NewAlertService(ServiceProvider provider)
    {
        return new(provider.GetRequiredService<HealthCheckService>(),
                   provider.GetRequiredService<IServiceScopeFactory>(),
                   NullLogger<TelemetryAlertService>.Instance);
    }

    [Fact]
    public async Task AnUnhealthyServiceEmailsTheAdministrator()
    {
        // Arrange
        ServiceProvider provider = await BuildAsync(HealthStatus.Unhealthy);
        using TelemetryAlertService alerts = NewAlertService(provider);

        // Act - the service polls once immediately on start, so this needs no waiting out of the
        // poll interval.
        await alerts.StartAsync(CancellationToken.None);

        try
        {
            MailpitClient.MailpitMessageSummary summary = await _mailpit.AwaitMessageAsync(AdminAddress);
            MailpitClient.MailpitMessage message = await _mailpit.GetMessageAsync(summary.ID);

            // Assert
            summary.To.Should().ContainSingle()
                   .Which.Address.Should().Be(AdminAddress, "the recipient has to come out of the database, not configuration");

            message.Subject.Should().Contain("unhealthy");

            // The check's own description reaches the reader, rather than a second wording invented
            // by the alert path.
            message.HTML.Should().Contain("Nothing is reaching the database.");
        }
        finally
        {
            await alerts.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The alert arrives in the administrator's own language, from a service that has no request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what the stored language column exists for, proven end to end.</b>
    /// <c>TelemetryAlertService</c> runs on a timer, so there is no <c>HttpContext</c> and no
    /// <c>Accept-Language</c> anywhere in the path — the only way this message can be Danish is by
    /// reading <c>HSUser.Language</c> and composing inside that culture.
    /// </para>
    /// <para>
    /// The health check's own description is asserted to be <i>unchanged</i> beside it. That is the
    /// machine-text boundary in the one place it is easiest to get wrong: the prose around the list
    /// is ours to translate, the list is somebody else's text and reaches the banner and
    /// <c>/health</c> untranslated.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnAlertIsWrittenInTheAdministratorsLanguage()
    {
        // Arrange
        ServiceProvider provider = await BuildAsync(HealthStatus.Unhealthy, adminLanguage: "da");
        using TelemetryAlertService alerts = NewAlertService(provider);

        // Act
        await alerts.StartAsync(CancellationToken.None);

        try
        {
            MailpitClient.MailpitMessageSummary summary = await _mailpit.AwaitMessageAsync(AdminAddress);
            MailpitClient.MailpitMessage message = await _mailpit.GetMessageAsync(summary.ID);

            // Assert
            message.Subject.Should().Be("Homespool har et problem");
            message.Subject.Should().NotContain("unhealthy");

            message.HTML.Should().Contain("Homespool rapporterede et problem:");
            message.HTML.Should().Contain("Print påvirkes ikke");

            message.HTML.Should().Contain(
                "Nothing is reaching the database.",
                "the check's own description is not ours to translate - the banner and /health carry it untranslated");
        }
        finally
        {
            await alerts.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A healthy service sends nothing. Guards the other direction: an alerting path that fires on
    /// every poll would be worse than none.
    /// </summary>
    [Fact]
    public async Task AHealthyServiceEmailsNobody()
    {
        // Arrange
        ServiceProvider provider = await BuildAsync(HealthStatus.Healthy);
        using TelemetryAlertService alerts = NewAlertService(provider);

        // Act
        await alerts.StartAsync(CancellationToken.None);

        try
        {
            // Waits out a window the unhealthy test above shows is ample for delivery. Asserting on
            // the answer rather than on an exception matters: expecting AwaitMessageAsync to throw
            // looks equivalent, but any exception satisfies it - a Mailpit container that is simply
            // down would make this pass while proving nothing.
            bool nobodyWasEmailed = await _mailpit.NoMessageArrivesAsync(AdminAddress, TimeSpan.FromSeconds(2));

            nobodyWasEmailed.Should().BeTrue("a healthy service has nothing to report");
        }
        finally
        {
            await alerts.StopAsync(CancellationToken.None);
        }
    }
}
