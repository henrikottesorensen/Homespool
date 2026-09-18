using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Host.Queue;

namespace Homespool.Host.Test;

/// <summary>
/// What <see cref="TransferOfferStore"/> promises about who may open an offer and when it leaves
/// service - the binding to a printer, the release a printer's own terminal event triggers, the
/// two limits on an offer nobody came back for, and the hook that lets a key kept beside an offer
/// follow it out.
/// </summary>
public sealed class TransferOfferStoreTests : IDisposable
{
    private const int Printer = 7;
    private const int OtherPrinter = 8;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"hs-offers-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _clock = new();
    private readonly TransferOfferStore _store;

    public TransferOfferStoreTests()
    {
        _store = new TransferOfferStore(_clock, NullLogger<TransferOfferStore>.Instance);

        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// A release names the offer and the printer, and both have to match: the printer the offer was
    /// made to cannot give back somebody else's, and a stranger cannot give back this one.
    /// </summary>
    [Fact]
    public void AReleaseByHashRetiresOnlyAnOfferBoundToThatPrinter()
    {
        // Arrange
        string token = Offer(Printer);

        // Act
        _store.Release(OtherPrinter, token);

        // Assert
        _store.TryOpen(token, Printer, out ITransferContent? stillThere).Should().BeTrue("another printer's release must not touch it");
        stillThere!.Dispose();

        // Act
        _store.Release(Printer, token);

        // Assert
        _store.TryOpen(token, Printer, out _).Should().BeFalse("the printer it was made to has said the transfer is over");
    }

    /// <summary>
    /// The actor never learns the token on the encrypted and raw-fetch paths, so a release without
    /// one retires everything idle for that printer - and nothing for any other.
    /// </summary>
    [Fact]
    public void AReleaseWithoutAHashRetiresEveryIdleOfferForThatPrinterAlone()
    {
        // Arrange
        string first = Offer(Printer);
        string second = Offer(Printer);
        string foreign = Offer(OtherPrinter);

        // Act
        _store.Release(Printer, hash: null);

        // Assert
        _store.TryOpen(first, Printer, out _).Should().BeFalse();
        _store.TryOpen(second, Printer, out _).Should().BeFalse("a timed-out send's leftover goes with it");

        _store.TryOpen(foreign, OtherPrinter, out ITransferContent? untouched).Should().BeTrue("the other printer's offer is not this printer's to release");
        untouched!.Dispose();
    }

    /// <summary>
    /// A release without a hash must not cut a fetch that is in progress. The encrypted download is
    /// served as separate range requests, and the printer's transfer slot is shared with other
    /// uploads, so "idle" is what stands between a stray terminal event and a truncated file.
    /// </summary>
    [Fact]
    public void AReleaseWithoutAHashLeavesAnOfferSomebodyIsReading()
    {
        // Arrange
        string token = Offer(Printer);
        _store.TryOpen(token, Printer, out ITransferContent? reading).Should().BeTrue();

        // Act
        _store.Release(Printer, hash: null);

        // Assert
        _store.TryOpen(token, Printer, out ITransferContent? again).Should().BeTrue("a borrowed offer is not idle");
        again!.Dispose();
        reading!.Dispose();
    }

    /// <summary>
    /// Every way out of the store raises the hook once with the token, so a key kept beside an offer
    /// can follow it: revoke, release, and a re-offer under the same token. The sweep has its own
    /// tests below.
    /// </summary>
    [Fact]
    public void EveryRetirementRaisesRetiredWithTheToken()
    {
        // Arrange
        List<string> retired = [];
        _store.Retired += retired.Add;

        string revoked = Offer(Printer);
        string released = Offer(Printer);
        string replaced = Offer(Printer);

        // Act
        _store.Revoke(revoked);
        _store.Release(Printer, released);
        _store.Offer(replaced, WriteFile(), Printer).Should().BeTrue();

        // Assert
        retired.Should().Equal(revoked, released, replaced);
    }

    /// <summary>
    /// The key store follows the offer store out by construction: retiring the offer is what zeroes
    /// the key, whichever path retired it, so no caller has to remember two revokes.
    /// </summary>
    [Fact]
    public void RetiringAnOfferRevokesTheKeyRegisteredBesideIt()
    {
        // Arrange
        EncryptedTransferOffers keys = new(_store);
        string ivHex = Offer(Printer);
        keys.Register(ivHex, RandomNumberGenerator.GetBytes(TransferCipher.KeyLength), ivHex, Printer);
        keys.Find(ivHex).Should().NotBeNull();

        // Act
        _store.Release(Printer, hash: null);

        // Assert
        keys.Find(ivHex).Should().BeNull("a key that outlived its bytes would be a secret kept for nothing");
    }

    /// <summary>
    /// A printer fetches as soon as it has accepted the command, so an offer nobody has opened
    /// within minutes belongs to a printer that went away - and it takes its key with it.
    /// </summary>
    [Fact]
    public void ASweepClosesAnOfferNoPrinterEverOpened()
    {
        // Arrange
        EncryptedTransferOffers keys = new(_store);
        string ivHex = Offer(Printer);
        keys.Register(ivHex, RandomNumberGenerator.GetBytes(TransferCipher.KeyLength), ivHex, Printer);

        _clock.Advance(TransferOfferStore.CollectWithin);

        // Act
        _store.SweepIdle();

        // Assert
        keys.Find(ivHex).Should().BeNull("the sweep alone retired it, and the key goes when the bytes do");
    }

    [Fact]
    public void ASweepLeavesAnOfferStillInsideItsWindow()
    {
        // Arrange
        string token = Offer(Printer);

        _clock.Advance(TransferOfferStore.CollectWithin - TimeSpan.FromSeconds(1));

        // Act
        _store.SweepIdle();

        // Assert
        _store.TryOpen(token, Printer, out ITransferContent? content).Should().BeTrue();
        content!.Dispose();
    }

    /// <summary>
    /// Once a printer has opened an offer the longer limit applies, because firmware does come back:
    /// it retries a dropped download and resumes one across a reboot, under the same token.
    /// </summary>
    [Fact]
    public void AnOpenedOfferIsKeptLongerForThePrinterToComeBackTo()
    {
        // Arrange
        List<string> retired = [];
        _store.Retired += retired.Add;

        string token = Offer(Printer);
        _store.TryOpen(token, Printer, out ITransferContent? firstAttempt).Should().BeTrue();
        firstAttempt!.Dispose();

        // Act
        _clock.Advance(TransferOfferStore.ResumeWithin - TimeSpan.FromSeconds(1));
        _store.SweepIdle();

        // Assert
        retired.Should().BeEmpty("a dropped download may still resume");

        // Act
        _clock.Advance(TimeSpan.FromSeconds(1));
        _store.SweepIdle();

        // Assert
        retired.Should().Equal(token);
    }

    /// <summary>
    /// Neither limit bounds a transfer in progress. A borrowed offer is passed over however old it
    /// is, and collected by the first sweep after it is given back.
    /// </summary>
    [Fact]
    public void ASweepLeavesAnOfferSomebodyIsReadingUntilItIsGivenBack()
    {
        // Arrange
        List<string> retired = [];
        _store.Retired += retired.Add;

        string token = Offer(Printer);
        _store.TryOpen(token, Printer, out ITransferContent? reading).Should().BeTrue();

        _clock.Advance(TransferOfferStore.ResumeWithin + TimeSpan.FromHours(1));

        // Act
        _store.SweepIdle();

        // Assert
        retired.Should().BeEmpty("a slow transfer is not an abandoned one");

        // Act
        reading!.Dispose();
        _store.SweepIdle();

        // Assert
        retired.Should().Equal(token);
    }

    /// <summary>
    /// The limit is exact for whoever presents the token, whatever the sweep's timer is doing: an
    /// offer past it does not open, and is retired by the attempt.
    /// </summary>
    [Fact]
    public void AnOfferPastItsLimitDoesNotOpenEvenBeforeASweepReachesIt()
    {
        // Arrange
        List<string> retired = [];
        _store.Retired += retired.Add;

        string token = Offer(Printer);

        _clock.Advance(TransferOfferStore.CollectWithin);

        // Act
        bool opened = _store.TryOpen(token, Printer, out _);

        // Assert
        opened.Should().BeFalse();
        retired.Should().Equal(token);
    }

    /// <summary>
    /// Another printer presenting the token is refused before its age is looked at, so a stranger
    /// cannot retire an offer by asking for it.
    /// </summary>
    [Fact]
    public void AnotherPrinterAskingForAnExpiredOfferDoesNotRetireIt()
    {
        // Arrange
        List<string> retired = [];
        _store.Retired += retired.Add;

        string token = Offer(Printer);

        _clock.Advance(TransferOfferStore.CollectWithin);

        // Act
        bool opened = _store.TryOpen(token, OtherPrinter, out _);

        // Assert
        opened.Should().BeFalse();
        retired.Should().BeEmpty();
    }

    /// <summary>
    /// Past the queue's patience the file is offered again under a fresh token, which is the whole
    /// argument for where the longer limit sits. The two are separate constants so that the transfer
    /// code does not reach into the queue; this is what keeps them from drifting.
    /// </summary>
    [Fact]
    public void TheResumeLimitIsTheQueuesPatienceWithATransfer()
    {
        TransferOfferStore.ResumeWithin.Should().Be(QueueAdvancer.TransferStaleAfter);
    }

    private string Offer(int printerId)
    {
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        _store.Offer(token, WriteFile(), printerId).Should().BeTrue();

        return token;
    }

    private string WriteFile()
    {
        string path = Path.Combine(_directory, $"{Guid.NewGuid():N}.gcode");
        File.WriteAllBytes(path, new byte[64]);

        return path;
    }
}
