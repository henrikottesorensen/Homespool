using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

using Homespool.Host.Accounts;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// One change to how an account signs in, then a five-minute cooldown - per account, on
/// <see cref="AttemptLimiter"/>'s table, and lifted with the account's other backoffs.
/// </summary>
public sealed class CredentialChangeLimitTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-changelimit-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeLogger<CredentialChangeLimit> _logger = new();

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task ASecondChangeInsideTheCooldownIsRefusedAndLogged()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        CredentialChangeLimit limit = NewLimit(rig);

        // Act
        bool first = await limit.TryStartAsync(user.Id, CancellationToken.None);
        bool second = await limit.TryStartAsync(user.Id, CancellationToken.None);

        // Assert
        (first, second).Should().Be((true, false));
        (await rig.Context.UserActionAttempts.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken))
            .Action.Should().Be(LimitedAction.ChangeSignIn, "its own row, so it neither waits on nor shortens another action's");
        FakeLogRecord record = _logger.Collector.GetSnapshot().Should().ContainSingle().Subject;
        record.Level.Should().Be(LogLevel.Warning);
        record.StructuredState.Should().Contain(property => property.Key == "UserId" && property.Value == user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task TheCooldownEndsAfterFiveMinutes()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        CredentialChangeLimit limit = NewLimit(rig);
        await limit.TryStartAsync(user.Id, CancellationToken.None);

        // Act
        _time.Advance(CredentialChangeLimit.Cooldown - TimeSpan.FromSeconds(1));
        bool justBefore = await limit.TryStartAsync(user.Id, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        bool after = await limit.TryStartAsync(user.Id, CancellationToken.None);

        // Assert
        justBefore.Should().BeFalse();
        after.Should().BeTrue();
    }

    [Fact]
    public async Task EachAccountHasItsOwnCooldown()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        HSUser other = await rig.AddUserAsync("other@example.com");
        CredentialChangeLimit limit = NewLimit(rig);
        await limit.TryStartAsync(user.Id, CancellationToken.None);

        // Act
        bool theirs = await limit.TryStartAsync(other.Id, CancellationToken.None);

        // Assert
        theirs.Should().BeTrue("another account's change does not start this one's cooldown");
    }

    /// <summary>Asking whether there is room starts nothing, and says no while a cooldown runs.</summary>
    [Fact]
    public async Task HasRoomStartsNoCooldown()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        CredentialChangeLimit limit = NewLimit(rig);

        // Act
        bool askedTwice = await limit.HasRoomAsync(user.Id, CancellationToken.None) && await limit.HasRoomAsync(user.Id, CancellationToken.None);
        bool started = await limit.TryStartAsync(user.Id, CancellationToken.None);
        bool whileCooling = await limit.HasRoomAsync(user.Id, CancellationToken.None);

        // Assert
        (askedTwice, started, whileCooling).Should().Be((true, true, false));
    }

    /// <summary>The administrator's "clear the lockout" lifts it with the account's other backoffs, which is why it lives in this table.</summary>
    [Fact]
    public async Task ClearingTheAccountsBackoffsLiftsTheCooldown()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        CredentialChangeLimit limit = NewLimit(rig);
        await limit.TryStartAsync(user.Id, CancellationToken.None);

        // Act
        await Attempts(rig).ResetAllAsync(user.Id, CancellationToken.None);
        bool again = await limit.TryStartAsync(user.Id, CancellationToken.None);

        // Assert
        again.Should().BeTrue();
    }

    private CredentialChangeLimit NewLimit(LocalSchemeRig rig)
    {
        return new CredentialChangeLimit(Attempts(rig), _time, _logger);
    }

    private static AttemptLimiter Attempts(LocalSchemeRig rig)
    {
        return rig.NewRequest().RequestServices.GetRequiredService<AttemptLimiter>();
    }
}
