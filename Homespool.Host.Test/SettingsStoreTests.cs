using System;
using System.Collections.Generic;
using System.IO;

using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Testing;

using Homespool.Host.Configuration;

namespace Homespool.Host.Test;

/// <summary>
/// Saving settings: what is written, what is refused, and the credential rule that a form must not
/// be able to destroy.
/// </summary>
public class SettingsStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly SettingsFile _file;
    private readonly SettingsSecretProtector _protector;

    public SettingsStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "homespool-store-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_directory);

        _file = new SettingsFile(Path.Combine(_directory, "settings.json"));

        _protector = new SettingsSecretProtector(
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_directory, "keys"))),
            new FakeLogger<SettingsSecretProtector>());
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AValueIsWrittenAndVisibleAfterTheReload()
    {
        (SettingsStore store, IConfigurationRoot configuration) = Store();

        store.Save(new Dictionary<string, string?> { ["Smtp:Host"] = "mail.example.com" })
             .Saved
             .Should()
             .BeTrue();

        configuration["Smtp:Host"].Should().Be("mail.example.com", "the store reloads what it wrote");
    }

    [Fact]
    public void AValueOutsideItsRangeIsRefusedAndNothingIsWritten()
    {
        (SettingsStore store, IConfigurationRoot configuration) = Store();

        SettingsSaveResult result = store.Save(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "mail.example.com",
            ["Smtp:Port"] = "70000",
        });

        result.Saved.Should().BeFalse();
        result.Errors.Should().ContainKey("Smtp:Port");

        configuration["Smtp:Host"].Should().BeNull("a refused save writes none of its values, not just the bad one");
        _file.Exists.Should().BeFalse();
    }

    [Fact]
    public void AKeyThatIsNotEditableIsIgnored()
    {
        (SettingsStore store, IConfigurationRoot configuration) = Store();

        store.Save(new Dictionary<string, string?> { ["Listeners:UserPort"] = "9999" });

        configuration["Listeners:UserPort"].Should().BeNull();
    }

    [Fact]
    public void ASecretIsStoredAsCiphertextAndNeverReadBack()
    {
        (SettingsStore store, IConfigurationRoot configuration) = Store();

        store.Save(new Dictionary<string, string?> { ["Smtp:Password"] = "hunter2" });

        configuration["Smtp:ProtectedPassword"].Should().NotBeNullOrEmpty().And.NotBe("hunter2");
        configuration["Smtp:Password"].Should().BeNull("the plain property is never written");

        store.Current()["Smtp:Password"]
             .Should()
             .Be(SettingsStore.SecretPlaceholder, "a browser is told there is one, never what it is");
    }

    /// <summary>
    /// The trap a camera password already fell into: a form posts back what it was shown, so an
    /// administrator correcting an unrelated field would otherwise overwrite the stored secret with
    /// the mask and destroy it.
    /// </summary>
    [Fact]
    public void PostingThePlaceholderBackLeavesTheStoredSecretAlone()
    {
        (SettingsStore store, IConfigurationRoot configuration) = Store();

        store.Save(new Dictionary<string, string?> { ["Smtp:Password"] = "hunter2" });

        string? stored = configuration["Smtp:ProtectedPassword"];

        store.Save(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "mail.example.com",
            ["Smtp:Password"] = SettingsStore.SecretPlaceholder,
        });

        configuration["Smtp:ProtectedPassword"].Should().Be(stored, "the mask is not an answer");
        configuration["Smtp:Host"].Should().Be("mail.example.com", "the field that was edited still changed");
    }

    /// <summary>
    /// The case that must keep working, or a password could never be changed.
    /// </summary>
    [Fact]
    public void ATypedSecretReplacesTheStoredOne()
    {
        (SettingsStore store, IConfigurationRoot configuration) = Store();

        store.Save(new Dictionary<string, string?> { ["Smtp:Password"] = "hunter2" });

        string? first = configuration["Smtp:ProtectedPassword"];

        store.Save(new Dictionary<string, string?> { ["Smtp:Password"] = "something-else" });

        configuration["Smtp:ProtectedPassword"].Should().NotBe(first);
        _protector.Reveal(configuration["Smtp:ProtectedPassword"], "Smtp:ProtectedPassword")
                  .Should()
                  .Be("something-else");
    }

    [Fact]
    public void AnEmptySecretClearsIt()
    {
        (SettingsStore store, IConfigurationRoot configuration) = Store();

        store.Save(new Dictionary<string, string?> { ["Smtp:Password"] = "hunter2" });
        store.Save(new Dictionary<string, string?> { ["Smtp:Password"] = string.Empty });

        configuration["Smtp:ProtectedPassword"].Should().BeNullOrEmpty();
        store.Current()["Smtp:Password"].Should().BeEmpty();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private (SettingsStore store, IConfigurationRoot configuration) Store()
    {
        ConfigurationManager configuration = new();

        configuration.AddJsonFile(_file.Path, optional: true, reloadOnChange: false);

        return (new SettingsStore(configuration, _file, _protector), configuration);
    }
}
