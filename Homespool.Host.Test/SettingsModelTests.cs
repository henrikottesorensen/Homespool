using System;
using System.Collections.Generic;
using System.IO;

using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Testing;

using Homespool.Host.Configuration;
using Homespool.Host.Pages.Admin;

namespace Homespool.Host.Test;

/// <summary>
/// Which changes the settings page stops to ask about.
/// </summary>
/// <remarks>
/// <b>Here rather than end to end, for the on-to-off case.</b> Enabling the two-factor requirement
/// locks the administrator who enabled it out of every page until they enrol, so a test driving the
/// real page cannot reach it again to turn it off. That is the setting behaving as documented, not a
/// gap in it.
/// </remarks>
public class SettingsModelTests : IDisposable
{
    private readonly string _directory;
    private readonly SettingsFile _file;
    private readonly ConfigurationManager _configuration;
    private readonly SettingsStore _store;

    public SettingsModelTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "homespool-model-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_directory);

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

    [Fact]
    public void TurningItOnIsAskedAbout()
    {
        SettingsModel page = Page();

        page.Values = new Dictionary<string, string?> { ["Security:RequireTwoFactor"] = "true" };
        page.OnPost();

        page.AwaitingConfirmation.Should().ContainSingle()
            .Which.Path.Should().Be("Security:RequireTwoFactor");
    }

    /// <summary>
    /// Only the dangerous direction asks. A confirmation on the safe one is how somebody learns to
    /// click past the question that matters.
    /// </summary>
    [Fact]
    public void TurningItBackOffIsNot()
    {
        _store.Save(new Dictionary<string, string?> { ["Security:RequireTwoFactor"] = "true" })
              .Saved.Should().BeTrue();

        SettingsModel page = Page();

        page.Values = new Dictionary<string, string?> { ["Security:RequireTwoFactor"] = "false" };
        page.OnPost();

        page.AwaitingConfirmation.Should().BeEmpty();
    }

    [Fact]
    public void AlreadyOnIsNotAskedAboutAgain()
    {
        _store.Save(new Dictionary<string, string?> { ["Security:RequireTwoFactor"] = "true" });

        SettingsModel page = Page();

        page.Values = new Dictionary<string, string?> { ["Security:RequireTwoFactor"] = "true" };
        page.OnPost();

        page.AwaitingConfirmation.Should().BeEmpty("it is not a change");
    }

    [Fact]
    public void AgreeingLetsItThrough()
    {
        SettingsModel page = Page();

        page.Values = new Dictionary<string, string?> { ["Security:RequireTwoFactor"] = "true" };
        page.Confirmed = ["Security:RequireTwoFactor"];
        page.OnPost();

        page.AwaitingConfirmation.Should().BeEmpty();
        _store.Current()["Security:RequireTwoFactor"].Should().Be("true");
    }

    [Fact]
    public void ASettingThatCarriesNoConsequenceIsNeverAskedAbout()
    {
        SettingsModel page = Page();

        page.Values = new Dictionary<string, string?> { ["Invitations:LifetimeHours"] = "72" };
        page.OnPost();

        page.AwaitingConfirmation.Should().BeEmpty();
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

    private SettingsModel Page()
    {
        return new SettingsModel(_store, TestLocaliser.Shared());
    }
}
