using System;

using AwesomeAssertions;

using Microsoft.Extensions.Time.Testing;

using Homespool.Host.Authentication;

namespace Homespool.Host.Test;

/// <summary>
/// The ceremony ledger: an answered ceremony is recorded once, a ceremony from before this process
/// is refused, the list does not grow with time, and it does not grow without bound either.
/// </summary>
public sealed class PasskeyCeremonyLedgerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static PasskeyCeremonyLedger LedgerStartedAt(DateTimeOffset started)
    {
        return new PasskeyCeremonyLedger(new FakeTimeProvider(started));
    }

    private static string NewId()
    {
        return Guid.NewGuid().ToString("N");
    }

    [Fact]
    public void AnAnsweredCeremonyIsRecordedOnce()
    {
        PasskeyCeremonyLedger ledger = LedgerStartedAt(Noon);
        string id = NewId();

        ledger.IsSpent(id, Noon.AddMinutes(1)).Should().BeFalse("nothing has answered it yet");
        ledger.Spend(id, Noon.AddMinutes(1), Noon.AddMinutes(6), Noon.AddMinutes(2)).Should().Be(PasskeyCeremonyLedger.SpendResult.Spent, "the first verified answer is the one that counts");
        ledger.IsSpent(id, Noon.AddMinutes(1)).Should().BeTrue();
        ledger.Spend(id, Noon.AddMinutes(1), Noon.AddMinutes(6), Noon.AddMinutes(2)).Should().Be(PasskeyCeremonyLedger.SpendResult.AlreadySpent, "the second is a replay");
    }

    [Fact]
    public void ACeremonyIssuedBeforeThisProcessStartedIsRefused()
    {
        PasskeyCeremonyLedger ledger = LedgerStartedAt(Noon);
        string id = NewId();

        ledger.IsSpent(id, Noon.AddMinutes(-1)).Should().BeTrue("a restart forgets what was answered, and forgetting must refuse rather than admit");
        ledger.Spend(id, Noon.AddMinutes(-1), Noon.AddMinutes(4), Noon).Should().Be(PasskeyCeremonyLedger.SpendResult.BeforeThisProcess);
    }

    /// <summary>
    /// Expired entries are swept every <see cref="PasskeyCeremonyLedger.SweepInterval"/> spends rather
    /// than on each one, so a spend costs nothing most of the time and the list still shrinks.
    /// </summary>
    [Fact]
    public void ExpiredEntriesAreForgottenAtTheNextSweep()
    {
        PasskeyCeremonyLedger ledger = LedgerStartedAt(Noon);
        string stale = NewId();
        string live = NewId();
        ledger.Spend(stale, Noon, Noon.AddMinutes(5), Noon);
        ledger.Spend(live, Noon, Noon.AddMinutes(30), Noon);

        // Past the stale one's expiry, enough spends to bring a sweep round.
        for (int i = 0; i < PasskeyCeremonyLedger.SweepInterval; i += 1)
        {
            ledger.Spend(NewId(), Noon.AddMinutes(6), Noon.AddMinutes(11), Noon.AddMinutes(6));
        }

        ledger.Spent.Should().Be(PasskeyCeremonyLedger.SweepInterval + 1, "the stale one went at the sweep, the live one and the new ones stand");
        ledger.IsSpent(live, Noon).Should().BeTrue();
        ledger.IsSpent(stale, Noon).Should().BeFalse("an expired ceremony cannot be answered anyway, so forgetting it admits nothing");
    }

    /// <summary>
    /// Above the cap a further answer is refused rather than remembered, and the cap counts live
    /// entries only: once the old ones expire, spends succeed again. Refused rather than evicted,
    /// because an evicted entry is a replay window.
    /// </summary>
    [Fact]
    public void AFullLedgerRefusesUntilSomethingExpires()
    {
        PasskeyCeremonyLedger ledger = LedgerStartedAt(Noon);

        for (int i = 0; i < PasskeyCeremonyLedger.MaxSpent; i += 1)
        {
            ledger.Spend(NewId(), Noon, Noon.AddMinutes(5), Noon).Should().Be(PasskeyCeremonyLedger.SpendResult.Spent, "the cap has not been reached");
        }

        ledger.Spend(NewId(), Noon.AddMinutes(1), Noon.AddMinutes(6), Noon.AddMinutes(1)).Should().Be(PasskeyCeremonyLedger.SpendResult.Full, "every slot holds an answer that has not expired");
        ledger.Spent.Should().Be(PasskeyCeremonyLedger.MaxSpent);

        ledger.Spend(NewId(), Noon.AddMinutes(6), Noon.AddMinutes(11), Noon.AddMinutes(6)).Should().Be(PasskeyCeremonyLedger.SpendResult.Spent, "the sweep at the cap cleared the expired ones");
        ledger.Spent.Should().Be(1);
    }
}
