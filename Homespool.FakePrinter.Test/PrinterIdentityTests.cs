using System.Linq;

using AwesomeAssertions;

namespace Homespool.FakePrinter.Test;

/// <summary>
/// The identity's shape rules: <c>printerHash()</c>'s 50-symbol output alphabet, and the
/// 16-character header truncation that once broke real enrollment
/// (<c>notes/cross-channel-identity-bug.md</c>).
/// </summary>
public class PrinterIdentityTests
{
    /// <summary>Random fingerprints are 50 characters of the firmware's 0-9A-V alphabet.</summary>
    [Fact]
    public void ARandomFingerprintHasTheFirmwareShape()
    {
        PrinterIdentity identity = PrinterIdentity.CreateRandom();

        identity.Fingerprint.Should().HaveLength(50);
        identity.Fingerprint.Should().MatchRegex("^[0-9A-V]{50}$");
    }

    /// <summary>The header form is exactly the first 16 characters - a prefix, not a hash.</summary>
    [Fact]
    public void TheHeaderFingerprintIsTheFirst16Characters()
    {
        PrinterIdentity identity = PrinterIdentity.CreateRandom();

        identity.HeaderFingerprint.Should().HaveLength(16);
        identity.Fingerprint.Should().StartWith(identity.HeaderFingerprint);
    }

    /// <summary>Two identities never collide - the property that lets tests run fakes in parallel.</summary>
    [Fact]
    public void TwoRandomIdentitiesDiffer()
    {
        PrinterIdentity[] identities = Enumerable.Range(0, 20).Select(_ => PrinterIdentity.CreateRandom()).ToArray();

        identities.Select(i => i.Fingerprint).Distinct().Should().HaveCount(20);
        identities.Select(i => i.SerialNumber).Distinct().Should().HaveCount(20);
    }
}
