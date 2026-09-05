using System;

using AwesomeAssertions;

using Homespool.Host.Authentication;

namespace Homespool.Host.Test;

/// <summary>
/// The ceremony ledger: an id is spendable exactly once, an id the ledger never issued is not, the
/// list does not grow with time, and it does not grow without bound either.
/// </summary>
public sealed class PasskeyCeremonyLedgerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnIssuedCeremonyIsSpentOnce()
    {
        PasskeyCeremonyLedger ledger = new();
        string id = ledger.Begin(Noon, Noon.AddMinutes(5))!;

        ledger.TrySpend(id).Should().BeTrue("the first answer is the one that counts");
        ledger.TrySpend(id).Should().BeFalse("the second is a replay");
    }

    [Fact]
    public void ACeremonyThisLedgerNeverIssuedCannotBeSpent()
    {
        PasskeyCeremonyLedger ledger = new();

        ledger.TrySpend(Guid.NewGuid().ToString("N")).Should().BeFalse("a restart forgets what was outstanding, and forgetting must refuse rather than admit");
    }

    /// <summary>
    /// Expired entries are swept every <see cref="PasskeyCeremonyLedger.SweepInterval"/> begins rather
    /// than on each one, so a begin costs nothing most of the time and the list still shrinks.
    /// </summary>
    [Fact]
    public void ExpiredCeremoniesAreForgottenAtTheNextSweep()
    {
        PasskeyCeremonyLedger ledger = new();
        string stale = ledger.Begin(Noon, Noon.AddMinutes(5))!;
        string live = ledger.Begin(Noon, Noon.AddMinutes(30))!;

        // Past the stale one's expiry, enough begins to bring a sweep round.
        for (int i = 0; i < PasskeyCeremonyLedger.SweepInterval; i += 1)
        {
            ledger.Begin(Noon.AddMinutes(6), Noon.AddMinutes(11));
        }

        ledger.Outstanding.Should().Be(PasskeyCeremonyLedger.SweepInterval + 1, "the stale one went at the sweep, the live one and the new ones stand");
        ledger.TrySpend(stale).Should().BeFalse();
        ledger.TrySpend(live).Should().BeTrue();
    }

    /// <summary>
    /// Above the cap a new ceremony is refused rather than remembered, and the cap counts live
    /// entries only: once the old ones expire, begins succeed again.
    /// </summary>
    [Fact]
    public void AFullLedgerRefusesUntilSomethingExpires()
    {
        PasskeyCeremonyLedger ledger = new();

        for (int i = 0; i < PasskeyCeremonyLedger.MaxOutstanding; i += 1)
        {
            ledger.Begin(Noon, Noon.AddMinutes(5)).Should().NotBeNull("the cap has not been reached");
        }

        ledger.Begin(Noon.AddMinutes(1), Noon.AddMinutes(6)).Should().BeNull("every slot holds a ceremony that has not expired");
        ledger.Outstanding.Should().Be(PasskeyCeremonyLedger.MaxOutstanding);

        ledger.Begin(Noon.AddMinutes(6), Noon.AddMinutes(11)).Should().NotBeNull("the sweep at the cap cleared the expired ones");
        ledger.Outstanding.Should().Be(1);
    }
}
