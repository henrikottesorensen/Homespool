using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;
using Homespool.Data;
using Homespool.Host.Services;
using Homespool.Model.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
public sealed class TelemetryAlertMailpitTests : IAsyncLifetime, IDisposable
{
    private const string AdminAddress = "operator@printerservice.test";

    private readonly MailpitClient _mailpit = new();
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-alert-{Guid.NewGuid():N}.db");
    private ServiceProvider? _provider;

    public Task InitializeAsync() => _mailpit.ClearAsync();

    public Task DisposeAsync()
    {
        Dispose();

        return Task.CompletedTask;
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
    private async Task<ServiceProvider> BuildAsync(HealthStatus status)
    {
        ServiceCollection services = new();

        services.AddLogging();
        services.AddDbContext<HSDbContext>(o => o.UseSqlite($"Data Source={_databasePath}"));

        services.AddIdentityCore<HSUser>()
                .AddRoles<IdentityRole<long>>()
                .AddEntityFrameworkStores<HSDbContext>();

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

        // A stand-in for the telemetry check, so this test controls health without needing a broken
        // database - what is under test is the alerting, not the diagnosis.
        services.AddHealthChecks()
                .AddCheck("telemetry-persistence", () => new HealthCheckResult(
                    status, "Nothing is reaching the database."));

        _provider = services.BuildServiceProvider();

        await using (AsyncServiceScope scope = _provider.CreateAsyncScope())
        {
            HSDbContext context = scope.ServiceProvider.GetRequiredService<HSDbContext>();
            await context.Database.MigrateAsync();

            RoleManager<IdentityRole<long>> roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<long>>>();
            await roles.CreateAsync(new IdentityRole<long>(AdminBootstrap.AdminRole));

            UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
            HSUser admin = new() { UserName = AdminAddress, Email = AdminAddress, EmailConfirmed = true };

            (await users.CreateAsync(admin, "Correct-Horse-Battery-1!")).Succeeded.Should().BeTrue();
            (await users.AddToRoleAsync(admin, AdminBootstrap.AdminRole)).Succeeded.Should().BeTrue();
        }

        return _provider;
    }

    private TelemetryAlertService NewAlertService(ServiceProvider provider) =>
        new(provider.GetRequiredService<HealthCheckService>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TelemetryAlertService>.Instance);

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
            // No "await the absence of a message" primitive exists, so this waits out a window that
            // the unhealthy test above shows is ample for delivery.
            await Task.Delay(TimeSpan.FromSeconds(2));

            Func<Task> act = () => _mailpit.AwaitMessageAsync(AdminAddress);

            await act.Should().ThrowAsync<Exception>("a healthy service has nothing to report");
        }
        finally
        {
            await alerts.StopAsync(CancellationToken.None);
        }
    }
}
