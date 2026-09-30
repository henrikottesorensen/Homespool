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
        recipients.Current.Select(r => (r.Email, r.Culture)).Should().BeEquivalentTo(
        [
            ("dane@example.com", "da"),
            ("brit@example.com", (string?)null),
            ("german@example.com", (string?)null),
        ]);
    }

    /// <summary>
    /// Each administrator's browsers come with the list, so a push needs no read at send time. An
    /// administrator with no address is on it for their browser; one who turned health notifications
    /// off keeps the address and loses the browsers; one with neither is not on it at all.
    /// </summary>
    [Fact]
    public async Task BrowsersAreReadWithTheListAndTheSwitchSilencesOnlyThem()
    {
        // Arrange
        AlertRecipients recipients = await RecipientsAsync(async context =>
        {
            HSUser listening = await AddUserAsync(context, "listening@example.com", null, administrator: true);
            HSUser muted = await AddUserAsync(context, "muted@example.com", null, administrator: true,
                                              muted: "PrinterLost ServiceHealth");
            HSUser addressless = await AddUserAsync(context, null, null, administrator: true, userName: "addressless");
            await AddUserAsync(context, null, null, administrator: true, userName: "unreachable");
            HSUser member = await AddUserAsync(context, "member@example.com", null, administrator: false);

            await AddBrowserAsync(context, listening, "listening-1");
            await AddBrowserAsync(context, listening, "listening-2");
            await AddBrowserAsync(context, muted, "muted");
            await AddBrowserAsync(context, addressless, "addressless");
            await AddBrowserAsync(context, member, "member");
        });

        // Act
        await recipients.RefreshAsync(TestContext.Current.CancellationToken);

        // Assert
        recipients.Current.Select(r => (r.Email, Browsers: string.Join(' ', r.Browsers.Select(b => b.Name).Order())))
                  .Should().BeEquivalentTo(
                  [
                      ("listening@example.com", "listening-1 listening-2"),
                      ("muted@example.com", string.Empty),
                      ((string?)null, "addressless"),
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
            HSUser admin = await AddUserAsync(context, "admin@example.com", null, administrator: true);
            await AddUserAsync(context, "deputy@example.com", null, administrator: true);
            await AddBrowserAsync(context, admin, "admin");
        });

        await recipients.RefreshAsync(TestContext.Current.CancellationToken);
        recipients.Current.Should().HaveCount(2, "the fixture has to reach the state being tested");
        recipients.Current.Single(r => r.Email == "admin@example.com").Browsers.Should().ContainSingle();

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
        string?[] keptThroughOutage = recipients.Current.Select(r => r.Email).ToArray();
        int browsersKept = recipients.Current.Sum(r => r.Browsers.Count);

        _connectionString = working;
        await recipients.RefreshAsync(TestContext.Current.CancellationToken);

        // Assert
        keptThroughOutage.Should().BeEquivalentTo(["admin@example.com", "deputy@example.com"],
                                                  "the list from the last read that worked is what an outage is reported to");
        browsersKept.Should().Be(1, "a browser is how an outage reaches a deployment without mail");
        _logger.Collector.GetSnapshot().Should().ContainSingle(r => r.Level == LogLevel.Warning);
        recipients.Current.Select(r => r.Email).Should().Equal(["admin@example.com"],
                                                             "the closure is seen by the first read that works");
    }

    private static async Task<HSUser> AddUserAsync(HomespoolDbContext context,
                                                   string? email,
                                                   string? language,
                                                   bool administrator,
                                                   bool closed = false,
                                                   string? muted = null,
                                                   string? userName = null)
    {
        HSUser user = new(userName ?? email!.Split('@')[0])
        {
            Email = email,
            NormalizedEmail = email?.ToUpperInvariant(),
            EmailConfirmed = email is not null,
            Language = language,
            DeactivatedAt = closed ? DateTimeOffset.UtcNow : null,
            MutedNotifications = muted,
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

        return user;
    }

    private static async Task AddBrowserAsync(HomespoolDbContext context, HSUser owner, string name)
    {
        context.WebPushDestinations.Add(new WebPushDestination
        {
            UserId = owner.Id,
            Name = name,
            Endpoint = $"https://fcm.googleapis.com/fcm/send/{name}",
            P256dh = "p256dh",
            Auth = "auth",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
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
