using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

using Homespool.Data;
using Homespool.Host.Configuration;
using Homespool.Host.Mail;
using Homespool.Host.Pages.Admin;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// What the settings page stops to ask about, and the one change it refuses outright.
/// </summary>
/// <remarks>
/// <b>Here rather than end to end for anything after the requirement is on.</b> Enabling it locks the
/// administrator who enabled it out of every page until they enrol, so a test driving the real page
/// cannot reach it again. That is the setting behaving as its options class describes, not a gap.
/// </remarks>
public class SettingsModelTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;
    private readonly SettingsFile _file;
    private readonly ConfigurationManager _configuration;
    private readonly SettingsStore _store;

    public SettingsModelTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "homespool-model-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_directory);

        _databasePath = Path.Combine(_directory, "identity.db");
        _file = new SettingsFile(Path.Combine(_directory, "settings.json"));

        _configuration = new ConfigurationManager();

        _configuration.AddJsonFile(_file.Path, optional: true, reloadOnChange: false);

        _store = new SettingsStore(
            _configuration,
            _file,
            new SettingsSecretProtector(
                DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_directory, "keys"))),
                new FakeLogger<SettingsSecretProtector>()));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Turning the requirement on without an authenticator of your own would send you to enrol and
    /// leave you unable to reach the page that would undo it.
    /// </summary>
    [Fact]
    public async Task AnAdministratorWithoutAnAuthenticatorIsRefused()
    {
        (SettingsModel page, _) = await PageAsync(twoFactor: false);

        page.Values = new Dictionary<string, string?> { ["Security:RequireTwoFactor"] = "true" };

        await page.OnPost();

        page.Errors.Should().ContainKey("Security:RequireTwoFactor");
        page.AwaitingConfirmation.Should().BeEmpty("a refusal is not a question");
        _store.Current()["Security:RequireTwoFactor"].Should().NotBe("true");
    }

    [Fact]
    public async Task WithOneOfTheirOwnTheyAreAskedInstead()
    {
        (SettingsModel page, _) = await PageAsync(twoFactor: true);

        page.Values = new Dictionary<string, string?> { ["Security:RequireTwoFactor"] = "true" };

        await page.OnPost();

        page.Errors.Should().BeEmpty();
        page.AwaitingConfirmation.Should().ContainSingle()
            .Which.Path.Should().Be("Security:RequireTwoFactor");
        _store.Current()["Security:RequireTwoFactor"].Should().NotBe("true", "being asked is not agreeing");
    }

    [Fact]
    public async Task AgreeingAppliesIt()
    {
        (SettingsModel page, _) = await PageAsync(twoFactor: true);

        page.Values = new Dictionary<string, string?> { ["Security:RequireTwoFactor"] = "true" };
        page.Confirmed = ["Security:RequireTwoFactor"];

        await page.OnPost();

        page.AwaitingConfirmation.Should().BeEmpty();
        _store.Current()["Security:RequireTwoFactor"].Should().Be("true");
    }

    /// <summary>
    /// Only the dangerous direction asks. A confirmation on the safe one is how somebody learns to
    /// click past the question that matters.
    /// </summary>
    [Fact]
    public async Task TurningItBackOffIsNeitherRefusedNorAskedAbout()
    {
        _store.Save(new Dictionary<string, string?> { ["Security:RequireTwoFactor"] = "true" })
              .Saved.Should().BeTrue();

        (SettingsModel page, _) = await PageAsync(twoFactor: false);

        page.Values = new Dictionary<string, string?> { ["Security:RequireTwoFactor"] = "false" };

        await page.OnPost();

        page.Errors.Should().BeEmpty();
        page.AwaitingConfirmation.Should().BeEmpty();
        _store.Current()["Security:RequireTwoFactor"].Should().Be("false");
    }

    [Fact]
    public async Task AlreadyOnIsNotAskedAboutAgain()
    {
        _store.Save(new Dictionary<string, string?> { ["Security:RequireTwoFactor"] = "true" });

        (SettingsModel page, _) = await PageAsync(twoFactor: true);

        page.Values = new Dictionary<string, string?> { ["Security:RequireTwoFactor"] = "true" };

        await page.OnPost();

        page.AwaitingConfirmation.Should().BeEmpty("it is not a change");
    }

    [Fact]
    public async Task ASettingThatCarriesNoConsequenceIsNeverAskedAbout()
    {
        (SettingsModel page, _) = await PageAsync(twoFactor: false);

        page.Values = new Dictionary<string, string?> { ["Invitations:LifetimeHours"] = "72" };

        await page.OnPost();

        page.Errors.Should().BeEmpty();
        page.AwaitingConfirmation.Should().BeEmpty();
    }

    /// <summary>
    /// The question is "would this work?", so it must use the values on the form. Mail settings need
    /// a restart, so the running configuration is the old answer by definition.
    /// </summary>
    [Fact]
    public async Task TheMailTestUsesWhatIsOnTheFormRatherThanWhatIsRunning()
    {
        FakeSmtpTransport transport = new();
        (SettingsModel page, _) = await PageAsync(twoFactor: false, transport);

        page.Values = new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "typed.example.com",
            ["Smtp:Port"] = "2525",
        };

        await page.OnPostTestMail(TestContext.Current.CancellationToken);

        transport.ConnectCall!.Value.host.Should().Be("typed.example.com", "nothing was saved, and that is the point");
        transport.ConnectCall!.Value.port.Should().Be(2525);
        page.StatusSuccess.Should().BeTrue();
    }

    /// <summary>
    /// A password nobody was shown comes back as the mask, and must not be tested as though the mask
    /// were the password - the same rule that stops a save destroying it.
    /// </summary>
    [Fact]
    public async Task TheMailTestUsesTheStoredPasswordWhenTheMaskComesBack()
    {
        _store.Save(new Dictionary<string, string?> { ["Smtp:Password"] = "hunter2" }).Saved.Should().BeTrue();

        FakeSmtpTransport transport = new();
        (SettingsModel page, _) = await PageAsync(twoFactor: false, transport);

        page.Values = new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "mail.example.com",
            ["Smtp:UserName"] = "postmaster",
            ["Smtp:Password"] = SettingsStore.SecretPlaceholder,
        };

        await page.OnPostTestMail(TestContext.Current.CancellationToken);

        transport.AuthenticateCall!.Value.password.Should().Be("hunter2");
    }

    [Fact]
    public async Task AFailedMailTestSaysWhatTheServerSaid()
    {
        FakeSmtpTransport transport = new() { ThrowOnConnect = new InvalidOperationException("no route to host") };
        (SettingsModel page, _) = await PageAsync(twoFactor: false, transport);

        page.Values = new Dictionary<string, string?> { ["Smtp:Host"] = "nowhere.example.com" };

        await page.OnPostTestMail(TestContext.Current.CancellationToken);

        page.StatusSuccess.Should().BeFalse();
        page.StatusMessage.Should().Contain("no route to host");
    }

    [Fact]
    public async Task TheMailTestSavesNothing()
    {
        FakeSmtpTransport transport = new();
        (SettingsModel page, _) = await PageAsync(twoFactor: false, transport);

        page.Values = new Dictionary<string, string?> { ["Smtp:Host"] = "typed.example.com" };

        await page.OnPostTestMail(TestContext.Current.CancellationToken);

        _store.Current()["Smtp:Host"].Should().BeEmpty("testing is not saving");
    }

    /// <summary>
    /// Mail is turned on by naming a server rather than by a flag, and what that changes - new
    /// accounts having to confirm before they can sign in - lands at the next restart, where nobody
    /// is watching.
    /// </summary>
    [Fact]
    public async Task NamingAMailServerIsAskedAbout()
    {
        (SettingsModel page, _) = await PageAsync(twoFactor: false);

        page.Values = new Dictionary<string, string?> { ["Smtp:Host"] = "mail.example.com" };

        await page.OnPost();

        page.AwaitingConfirmation.Should().ContainSingle()
            .Which.Path.Should().Be("Smtp:Host");
        _store.Current()["Smtp:Host"].Should().BeEmpty("being asked is not agreeing");
    }

    [Fact]
    public async Task ChangingAnotherMailFieldIsNotAskedAbout()
    {
        _store.Save(new Dictionary<string, string?> { ["Smtp:Host"] = "mail.example.com" }).Saved.Should().BeTrue();

        (SettingsModel page, _) = await PageAsync(twoFactor: false);

        page.Values = new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "mail.example.com",
            ["Smtp:Port"] = "2525",
        };

        await page.OnPost();

        page.AwaitingConfirmation.Should().BeEmpty("mail was already on; the port is not that decision");
        _store.Current()["Smtp:Port"].Should().Be("2525");
    }

    /// <summary>
    /// Clearing it is the safe direction - it makes account creation more permissive rather than
    /// less - so it is applied without a question, like every other off switch here.
    /// </summary>
    [Fact]
    public async Task TurningMailOffIsNotAskedAbout()
    {
        _store.Save(new Dictionary<string, string?> { ["Smtp:Host"] = "mail.example.com" });

        (SettingsModel page, _) = await PageAsync(twoFactor: false);

        page.Values = new Dictionary<string, string?> { ["Smtp:Host"] = string.Empty };

        await page.OnPost();

        page.AwaitingConfirmation.Should().BeEmpty();
        _store.Current()["Smtp:Host"].Should().BeEmpty();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        _configuration.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private async Task<(SettingsModel page, HomespoolDbContext context)> PageAsync(
        bool twoFactor,
        FakeSmtpTransport? transport = null)
    {
        HomespoolDbContext context = new(new DbContextOptionsBuilder<HomespoolDbContext>()
                                         .UseSqlite($"Data Source={_databasePath}")
                                         .Options);

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) =
            IdentityTestHarness.BuildIdentityServices(context);

        HSUser admin = new("admin") { Email = "admin@example.com", TwoFactorEnabled = twoFactor };

        (await users.CreateAsync(admin)).Succeeded.Should().BeTrue();

        IdentityTestHarness.SignInAsPrincipal(httpContext, admin);

        SettingsModel page = new(_store,
                                 users,
                                 new SmtpConnectivityCheck(new FakeSmtpTransportFactory(transport ?? new FakeSmtpTransport()),
                                                           NullLogger<SmtpConnectivityCheck>.Instance),
                                 TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
        };

        return (page, context);
    }
}
