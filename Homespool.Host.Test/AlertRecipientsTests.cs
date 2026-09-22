using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Health;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Who a health alert goes to, and the two properties that make the list worth having: it follows
/// closures within a poll, and a database it cannot read does not take the list away.
/// </summary>
public sealed class AlertRecipientsTests : IAsyncDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-alert-recipients-{Guid.NewGuid():N}.db");

    private readonly FakeLogger _logger = new();

    private string _connectionString;

    private ServiceProvider? _provider;

    public AlertRecipientsTests()
    {
        _connectionString = $"Data Source={_databasePath}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Every open administrator, each with the language they chose - read in the same query as the
    /// address, so nothing is looked up at send time. A closed administrator and an ordinary account
    /// are not on it, and a language no longer shipped reads as no choice.
    /// </summary>
    [Fact]
    public async Task AlertsGoToEveryOpenAdministratorInTheirOwnLanguage()
    {
        // Arrange
        AlertRecipients recipients = await RecipientsAsync(async context =>
        {
            await AddUserAsync(context, "dane@example.com", "da", administrator: true);
            await AddUserAsync(context, "brit@example.com", null, administrator: true);
            await AddUserAsync(context, "german@example.com", "de-DE", administrator: true);
            await AddUserAsync(context, "closed@example.com", "da", administrator: true, closed: true);
            await AddUserAsync(context, "member@example.com", "da", administrator: false);
        });

        // Act
        await recipients.RefreshAsync(TestContext.Current.CancellationToken);

        // Assert
        recipients.Current.Should().BeEquivalentTo(
        [
            new AlertRecipient("dane@example.com", "da"),
            new AlertRecipient("brit@example.com", null),
            new AlertRecipient("german@example.com", null),
        ]);
    }

    /// <summary>
    /// The outage case the list is kept for. A read that fails - here, a database file that cannot be
    /// opened - neither throws nor empties the list: the alert about the outage still has somebody to
    /// go to. The next read that works drops the administrator closed in between.
    /// </summary>
    [Fact]
    public async Task AFailedReadKeepsTheLastListAndTheNextReadDropsAClosedAdministrator()
    {
        // Arrange
        AlertRecipients recipients = await RecipientsAsync(async context =>
        {
            await AddUserAsync(context, "admin@example.com", null, administrator: true);
            await AddUserAsync(context, "deputy@example.com", null, administrator: true);
        });

        await recipients.RefreshAsync(TestContext.Current.CancellationToken);
        recipients.Current.Should().HaveCount(2, "the fixture has to reach the state being tested");

        await using (AsyncServiceScope scope = _provider!.CreateAsyncScope())
        {
            HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();
            HSUser deputy = await context.Users.SingleAsync(u => u.Email == "deputy@example.com", TestContext.Current.CancellationToken);
            deputy.DeactivatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        string working = _connectionString;

        // Act
        _connectionString = $"Data Source={Path.Combine(_databasePath + ".missing", "none.db")};Mode=ReadOnly";
        Func<Task> duringOutage = () => recipients.RefreshAsync(TestContext.Current.CancellationToken);
        await duringOutage.Should().NotThrowAsync("a failed read must not end the poll before the alert is sent");
        string[] keptThroughOutage = recipients.Current.Select(r => r.Email).ToArray();

        _connectionString = working;
        await recipients.RefreshAsync(TestContext.Current.CancellationToken);

        // Assert
        keptThroughOutage.Should().BeEquivalentTo(["admin@example.com", "deputy@example.com"],
                                                  "the list from the last read that worked is what an outage is reported to");
        _logger.Collector.GetSnapshot().Should().ContainSingle(r => r.Level == LogLevel.Warning);
        recipients.Current.Select(r => r.Email).Should().Equal(["admin@example.com"],
                                                             "the closure is seen by the first read that works");
    }

    private static async Task AddUserAsync(HomespoolDbContext context,
                                           string email,
                                           string? language,
                                           bool administrator,
                                           bool closed = false)
    {
        HSUser user = new(email.Split('@')[0])
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            Language = language,
            DeactivatedAt = closed ? DateTimeOffset.UtcNow : null,
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        if (administrator)
        {
            IdentityRole<long> role = await context.Roles.SingleOrDefaultAsync(r => r.Name == AdminBootstrap.AdminRole, TestContext.Current.CancellationToken) ??
                                      context.Roles.Add(new IdentityRole<long>(AdminBootstrap.AdminRole) { NormalizedName = "ADMIN" }).Entity;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            context.UserRoles.Add(new IdentityUserRole<long> { UserId = user.Id, RoleId = role.Id });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// A provider whose database is whatever <see cref="_connectionString"/> names when a scope is
    /// opened, so a test can take the database away between two reads.
    /// </summary>
    private async Task<AlertRecipients> RecipientsAsync(Func<HomespoolDbContext, Task> seed)
    {
        ServiceCollection services = new();
        services.AddDbContext<HomespoolDbContext>((_, options) => options.UseSqlite(_connectionString));
        _provider = services.BuildServiceProvider();

        await using (AsyncServiceScope scope = _provider.CreateAsyncScope())
        {
            HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await seed(context);
        }

        return new AlertRecipients(_provider.GetRequiredService<IServiceScopeFactory>(), _logger);
    }
}
