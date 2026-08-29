using System;

using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

using Homespool.Host.Configuration;
using Homespool.Host.Mail;

namespace Homespool.Host.Test;

/// <summary>
/// The one credential the settings file holds: how it is stored, and what happens when it cannot be
/// read back.
/// </summary>
public class SettingsSecretProtectorTests
{
    [Fact]
    public void ASecretRoundTrips()
    {
        SettingsSecretProtector protector = Protector();

        string? stored = protector.Protect("hunter2");

        stored.Should().NotBeNull().And.NotBe("hunter2", "storing it in the clear is the thing this prevents");

        protector.Reveal(stored, "Smtp:ProtectedPassword").Should().Be("hunter2");
    }

    [Fact]
    public void NothingToStoreStaysNothing()
    {
        SettingsSecretProtector protector = Protector();

        protector.Protect(null).Should().BeNull();
        protector.Protect(string.Empty).Should().BeNull();
        protector.Reveal(null, "Smtp:ProtectedPassword").Should().BeNull();
        protector.Reveal(string.Empty, "Smtp:ProtectedPassword").Should().BeNull();
    }

    /// <summary>
    /// The property that keeps a lost key ring from being a lost deployment: an undecryptable value
    /// is reported and treated as absent, not thrown.
    /// </summary>
    [Fact]
    public void AValueThisDeploymentCannotDecryptIsLoggedRatherThanThrown()
    {
        FakeLogger<SettingsSecretProtector> logger = new();

        SettingsSecretProtector mine = Protector(logger, "keys-a");
        SettingsSecretProtector theirs = Protector(new FakeLogger<SettingsSecretProtector>(), "keys-b");

        string? writtenElsewhere = theirs.Protect("hunter2");

        mine.Reveal(writtenElsewhere, "Smtp:ProtectedPassword").Should().BeNull();

        logger.Collector.GetSnapshot().Should().ContainSingle()
              .Which.Level.Should().Be(LogLevel.Error);
    }

    [Fact]
    public void TheLogNamesTheSettingRatherThanTheValue()
    {
        FakeLogger<SettingsSecretProtector> logger = new();

        Protector(logger, "keys-a").Reveal(Protector(null, "keys-b").Protect("hunter2"), "Smtp:ProtectedPassword");

        FakeLogRecord record = logger.Collector.GetSnapshot()[0];

        record.StructuredState.Should().Contain(pair => pair.Value == "Smtp:ProtectedPassword");
        record.Message.Should().NotContain("hunter2");
    }

    /// <summary>
    /// The purpose is versioned and must not be shared with the camera credential: Data Protection
    /// binds ciphertext to its purpose, so one string serving two secrets means a change made for one
    /// silently breaks the other.
    /// </summary>
    [Fact]
    public void ThePurposeIsItsOwnAndVersioned()
    {
        SettingsSecretProtector.Purpose.Should().Be("Homespool.Settings.Secret.v1");
        SettingsSecretProtector.Purpose.Should().NotBe(Cameras.CameraCredentialProtector.Purpose);
    }

    private static SettingsSecretProtector Protector(
        ILogger<SettingsSecretProtector>? logger = null,
        string keys = "keys")
    {
        return new SettingsSecretProtector(
            DataProtectionProvider.Create(new System.IO.DirectoryInfo(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                       "homespool-protect-" + keys + "-" + Guid.NewGuid().ToString("N")))),
            logger ?? new FakeLogger<SettingsSecretProtector>());
    }
}

/// <summary>
/// The post-configure that hides the storage from every consumer of <see cref="SmtpOptions"/>.
/// </summary>
public class SmtpPasswordUnprotectorTests
{
    [Fact]
    public void AStoredSecretBecomesThePlainPasswordEveryConsumerReads()
    {
        SettingsSecretProtector protector = NewProtector();

        SmtpOptions options = new() { ProtectedPassword = protector.Protect("hunter2")! };

        new SmtpPasswordUnprotector(protector).PostConfigure(null, options);

        options.Password.Should().Be("hunter2");
    }

    /// <summary>
    /// The upgrade path: a password written by the migration one-shot, or typed into the file by
    /// hand, is used as it stands rather than being cleared for not being encrypted.
    /// </summary>
    [Fact]
    public void APlainPasswordWithNoStoredSecretIsLeftAlone()
    {
        SmtpOptions options = new() { Password = "from-the-environment" };

        new SmtpPasswordUnprotector(NewProtector()).PostConfigure(null, options);

        options.Password.Should().Be("from-the-environment");
    }

    [Fact]
    public void TheStoredSecretWinsOverAPlainOne()
    {
        SettingsSecretProtector protector = NewProtector();

        SmtpOptions options = new()
        {
            Password = "stale-plaintext",
            ProtectedPassword = protector.Protect("what-was-saved")!,
        };

        new SmtpPasswordUnprotector(protector).PostConfigure(null, options);

        options.Password.Should().Be("what-was-saved");
    }

    /// <summary>
    /// An undecryptable secret must not leave a stale plaintext password in place: the operator saved
    /// something newer, and sending the old one would be worse than sending none.
    /// </summary>
    [Fact]
    public void AnUndecryptableSecretClearsThePasswordRatherThanFallingBack()
    {
        SmtpOptions options = new()
        {
            Password = "stale-plaintext",
            ProtectedPassword = NewProtector().Protect("what-was-saved")!,
        };

        new SmtpPasswordUnprotector(NewProtector()).PostConfigure(null, options);

        options.Password.Should().BeEmpty();
    }

    private static SettingsSecretProtector NewProtector()
    {
        return new SettingsSecretProtector(
            DataProtectionProvider.Create(new System.IO.DirectoryInfo(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                       "homespool-unprotect-" + Guid.NewGuid().ToString("N")))),
            new FakeLogger<SettingsSecretProtector>());
    }
}
