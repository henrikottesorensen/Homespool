using System;

using AwesomeAssertions;

using Homespool.Host.Authentication;

namespace Homespool.Host.Test;

/// <summary>
/// The ceremony ledger: an id is spendable exactly once, an id the ledger never issued is not, and
/// the list does not grow with time.
/// </summary>
public sealed class PasskeyCeremonyLedgerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnIssuedCeremonyIsSpentOnce()
    {
        PasskeyCeremonyLedger ledger = new();
        string id = ledger.Begin(Noon, Noon.AddMinutes(5));

        ledger.TrySpend(id).Should().BeTrue("the first answer is the one that counts");
        ledger.TrySpend(id).Should().BeFalse("the second is a replay");
    }

    [Fact]
    public void ACeremonyThisLedgerNeverIssuedCannotBeSpent()
    {
        PasskeyCeremonyLedger ledger = new();

        ledger.TrySpend(Guid.NewGuid().ToString("N")).Should().BeFalse("a restart forgets what was outstanding, and forgetting must refuse rather than admit");
    }

    [Fact]
    public void ExpiredCeremoniesAreForgottenAsNewOnesBegin()
    {
        PasskeyCeremonyLedger ledger = new();
        string stale = ledger.Begin(Noon, Noon.AddMinutes(5));
        string live = ledger.Begin(Noon.AddMinutes(1), Noon.AddMinutes(6));

        ledger.Begin(Noon.AddMinutes(5), Noon.AddMinutes(10));

        ledger.Outstanding.Should().Be(2, "the one that expired at noon plus five is gone, the other two stand");
        ledger.TrySpend(stale).Should().BeFalse();
        ledger.TrySpend(live).Should().BeTrue();
    }
}
