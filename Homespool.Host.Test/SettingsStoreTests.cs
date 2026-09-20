using System;
using System.Collections.Generic;
using System.IO;

using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Testing;

using Homespool.Host.Configuration;
using Homespool.Host.Mail;

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

        SaveMailServer(store);

        string? stored = configuration["Smtp:ProtectedPassword"];

        Dictionary<string, string?> form = Form(store);

        form["Smtp:FromName"] = "Workshop";

        store.Save(form).Saved.Should().BeTrue();

        configuration["Smtp:ProtectedPassword"].Should().Be(stored, "the mask is not an answer");
        configuration["Smtp:FromName"].Should().Be("Workshop", "the field that was edited still changed");
    }

    /// <summary>
    /// The stored password goes to the server and account it was saved for, and nowhere else. An
    /// administrator who was never told it must not be able to point it at a server of their own -
    /// with TLS or without, since whoever names the server can hold its certificate.
    /// </summary>
    [Theory]
    [InlineData("Smtp:Host", "attacker.example.net")]
    [InlineData("Smtp:Port", "2525")]
    [InlineData("Smtp:UseImplicitTls", "true")]
    [InlineData("Smtp:DisableTls", "true")]
    [InlineData("Smtp:UserName", "someone-else")]
    public void ChangingWhereThePasswordGoesBehindTheMaskIsRefused(string path, string value)
    {
        (SettingsStore store, IConfigurationRoot configuration) = Store();

        SaveMailServer(store);

        string? before = configuration[path];

        Dictionary<string, string?> form = Form(store);

        form[path] = value;
        form["Smtp:FromName"] = "Workshop";

        SettingsSaveResult result = store.Save(form);

        result.Saved.Should().BeFalse();
        result.Errors.Should().ContainKey("Smtp:Password").And.HaveCount(1);
        configuration[path].Should().Be(before, "a refused save writes nothing");
        configuration["Smtp:FromName"].Should().BeNull("not even the fields that were fine");
    }

    [Theory]
    [InlineData("Smtp:Host", "other.example.com")]
    [InlineData("Smtp:Port", "2525")]
    [InlineData("Smtp:UseImplicitTls", "true")]
    [InlineData("Smtp:DisableTls", "true")]
    [InlineData("Smtp:UserName", "someone-else")]
    public void ChangingItWithThePasswordTypedAgainIsSaved(string path, string value)
    {
        (SettingsStore store, IConfigurationRoot configuration) = Store();

        SaveMailServer(store);

        Dictionary<string, string?> form = Form(store);

        form[path] = value;
        form["Smtp:Password"] = "hunter2";

        store.Save(form).Saved.Should().BeTrue();

        configuration[path].Should().Be(value);
    }

    /// <summary>
    /// A host name has no case, so the same server spelled differently is not a different
    /// destination and costs nobody a re-typed password.
    /// </summary>
    [Fact]
    public void TheSameHostInAnotherCaseIsTheSameServer()
    {
        (SettingsStore store, _) = Store();

        SaveMailServer(store);

        Dictionary<string, string?> form = Form(store);

        form["Smtp:Host"] = "MAIL.Example.COM";

        store.Save(form).Saved.Should().BeTrue();
    }

    /// <summary>
    /// The settings file sits above the environment, so a server named on the page would otherwise
    /// be paired with a password the operator put in the environment.
    /// </summary>
    [Fact]
    public void APasswordFromTheEnvironmentIsHeldToTheSameRule()
    {
        (SettingsStore store, IConfigurationRoot configuration) = Store(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "mail.example.com",
            ["Smtp:UserName"] = "postmaster",
            ["Smtp:Password"] = "hunter2",
        });

        Dictionary<string, string?> form = Form(store);

        form["Smtp:Password"].Should().Be(SettingsStore.SecretPlaceholder, "the page shows the mask for it");

        form["Smtp:Host"] = "attacker.example.net";

        store.Save(form).Saved.Should().BeFalse();
        configuration["Smtp:Host"].Should().Be("mail.example.com");
    }

    [Fact]
    public void TheCandidateCarriesTheStoredPasswordToTheServerItWasSavedFor()
    {
        (SettingsStore store, _) = Store();

        SaveMailServer(store);

        Dictionary<string, string?> form = Form(store);

        form["Smtp:FromName"] = "Workshop";

        SettingsCandidate<SmtpOptions> candidate = store.CandidateFor<SmtpOptions>(form);

        candidate.Errors.Should().BeEmpty();
        candidate.Value!.Password.Should().Be("hunter2");
        candidate.Value.FromName.Should().Be("Workshop");
    }

    /// <summary>
    /// The test button connects without saving, so it needs the rule as much as a save does.
    /// </summary>
    [Fact]
    public void TheCandidateRefusesToCarryItAnywhereElse()
    {
        (SettingsStore store, _) = Store();

        SaveMailServer(store);

        Dictionary<string, string?> form = Form(store);

        form["Smtp:Host"] = "attacker.example.net";

        SettingsCandidate<SmtpOptions> candidate = store.CandidateFor<SmtpOptions>(form);

        candidate.Value.Should().BeNull();
        candidate.Errors.Should().ContainKey("Smtp:Password");
    }

    /// <summary>
    /// A submission that leaves the password out is refused exactly as the placeholder is: leaving it
    /// out keeps the stored one just the same, and a request that was not built by the page can leave
    /// out whatever it likes.
    /// </summary>
    [Fact]
    public void ASecretLeftOutOfTheSubmissionCannotCarryOverEither()
    {
        (SettingsStore store, IConfigurationRoot configuration) = Store();

        SaveMailServer(store);

        string? stored = configuration["Smtp:ProtectedPassword"];

        Dictionary<string, string?> form = Form(store);

        form.Remove("Smtp:Password");
        form["Smtp:Host"] = "attacker.example.net";

        SettingsSaveResult result = store.Save(form);

        result.Saved.Should().BeFalse();
        result.Errors.Should().ContainKey("Smtp:Password");
        configuration["Smtp:Host"].Should().Be("mail.example.com", "a refused save writes nothing");
        configuration["Smtp:ProtectedPassword"].Should().Be(stored);
    }

    /// <summary>The test button reaches the same server a save would, so it refuses the omission too.</summary>
    [Fact]
    public void TheCandidateRefusesASecretLeftOutOfTheSubmission()
    {
        (SettingsStore store, _) = Store();

        SaveMailServer(store);

        Dictionary<string, string?> form = Form(store);

        form.Remove("Smtp:Password");
        form["Smtp:Host"] = "attacker.example.net";

        SettingsCandidate<SmtpOptions> candidate = store.CandidateFor<SmtpOptions>(form);

        candidate.Value.Should().BeNull();
        candidate.Errors.Should().ContainKey("Smtp:Password");
    }

    /// <summary>
    /// With no password in force there is nothing to carry anywhere, so naming a server is an
    /// ordinary edit - which is how mail gets configured in the first place.
    /// </summary>
    [Fact]
    public void ASecretLeftOutWithNoneStoredDoesNotBlockTheSave()
    {
        (SettingsStore store, IConfigurationRoot configuration) = Store();

        SettingsSaveResult result = store.Save(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "mail.example.com",
            ["Smtp:Port"] = "587",
            ["Smtp:UserName"] = "postmaster",
        });

        result.Saved.Should().BeTrue();
        configuration["Smtp:Host"].Should().Be("mail.example.com");
    }

    /// <summary>
    /// The password may stay where it is for an edit that leaves the server alone, whether the form
    /// carried the mask or nothing at all.
    /// </summary>
    [Fact]
    public void ASecretLeftOutSurvivesAnEditThatKeepsTheServer()
    {
        (SettingsStore store, IConfigurationRoot configuration) = Store();

        SaveMailServer(store);

        string? stored = configuration["Smtp:ProtectedPassword"];

        Dictionary<string, string?> form = Form(store);

        form.Remove("Smtp:Password");
        form["Smtp:FromName"] = "Workshop";

        store.Save(form).Saved.Should().BeTrue();

        configuration["Smtp:ProtectedPassword"].Should().Be(stored);
        configuration["Smtp:FromName"].Should().Be("Workshop");
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

    private static void SaveMailServer(SettingsStore store)
    {
        store.Save(new Dictionary<string, string?>
             {
                 ["Smtp:Host"] = "mail.example.com",
                 ["Smtp:Port"] = "587",
                 ["Smtp:UserName"] = "postmaster",
                 ["Smtp:Password"] = "hunter2",
             })
             .Saved
             .Should()
             .BeTrue();
    }

    /// <summary>What the page posts back when nothing on it was touched.</summary>
    private static Dictionary<string, string?> Form(SettingsStore store)
    {
        Dictionary<string, string?> form = [];

        foreach ((string path, string value) in store.Current())
        {
            form[path] = value;
        }

        return form;
    }

    private (SettingsStore store, IConfigurationRoot configuration) Store(IDictionary<string, string?>? beneath = null)
    {
        ConfigurationManager configuration = new();

        if (beneath is not null)
        {
            configuration.AddInMemoryCollection(beneath);
        }

        configuration.AddJsonFile(_file.Path, optional: true, reloadOnChange: false);

        return (new SettingsStore(configuration, _file, _protector, TestLocaliser.Shared()), configuration);
    }
}
