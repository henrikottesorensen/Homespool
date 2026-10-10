using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

using Homespool.Host.Configuration;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="SettingsFileProvider"/> - the settings layer holding every restart-grade setting at
/// its startup value across a reload, while the rest of the file goes through.
/// </summary>
/// <remarks>
/// <c>Smtp:Host</c> stands for the restart grade and <c>Invitations:LifetimeHours</c> for the live
/// one throughout; which grade each setting has is <see cref="EditableSettings"/>' business, and the
/// end-to-end suite checks every one of them against a real host.
/// </remarks>
public sealed class SettingsFileProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "homespool-provider-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsFile _file;
    private readonly ConfigurationManager _configuration = new();

    public SettingsFileProviderTests()
    {
        Directory.CreateDirectory(_directory);

        _file = new SettingsFile(Path.Combine(_directory, "settings.json"));
    }

    public void Dispose()
    {
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

    [Fact]
    public void ALiveSettingChangesOnAReload()
    {
        Start(new JsonObject { ["Invitations"] = new JsonObject { ["LifetimeHours"] = "24" } });

        Save(new JsonObject { ["Invitations"] = new JsonObject { ["LifetimeHours"] = "12" } });

        _configuration["Invitations:LifetimeHours"].Should().Be("12");
    }

    [Fact]
    public void ARestartGradeSettingKeepsItsStartupValueAcrossAReload()
    {
        Start(new JsonObject { ["Smtp"] = new JsonObject { ["Host"] = "old.example.com" } });

        Save(new JsonObject { ["Smtp"] = new JsonObject { ["Host"] = "new.example.com" } });

        _configuration["Smtp:Host"].Should().Be("old.example.com", "the process keeps what startup decided");
        SavedValue("Smtp:Host").Should().Be("new.example.com", "what was saved is still there to show");
    }

    /// <summary>
    /// <b>Absent from the file at startup, absent until the restart</b>, so the layers beneath keep
    /// answering - the environment, here.
    /// </summary>
    [Fact]
    public void ARestartGradeSettingTheFileLackedAtStartupIsLeftToTheLayersBeneath()
    {
        Start(new JsonObject(), beneath: new Dictionary<string, string?> { ["Smtp:Host"] = "from-environment.example.com" });

        Save(new JsonObject { ["Smtp"] = new JsonObject { ["Host"] = "saved.example.com" } });

        _configuration["Smtp:Host"].Should().Be("from-environment.example.com");
        SavedValue("Smtp:Host").Should().Be("saved.example.com");
    }

    /// <summary>
    /// <b>Removing a restart-grade value is a change like any other</b>, and waits for the restart
    /// too: the layers beneath must not take over early.
    /// </summary>
    [Fact]
    public void ARestartGradeSettingRemovedFromTheFileIsStillHeld()
    {
        Start(new JsonObject { ["Smtp"] = new JsonObject { ["Host"] = "old.example.com" } },
              beneath: new Dictionary<string, string?> { ["Smtp:Host"] = "from-environment.example.com" });

        Save(new JsonObject());

        _configuration["Smtp:Host"].Should().Be("old.example.com");
        SavedValue("Smtp:Host").Should().Be("from-environment.example.com", "with the file silent, the environment is what the next start will use");
    }

    /// <summary>
    /// The file disappearing is the one reload the JSON parser never sees, and it holds the same.
    /// </summary>
    [Fact]
    public void ARestartGradeSettingIsHeldWhenTheFileIsDeleted()
    {
        Start(new JsonObject
        {
            ["Smtp"] = new JsonObject { ["Host"] = "old.example.com" },
            ["Invitations"] = new JsonObject { ["LifetimeHours"] = "12" },
        });

        File.Delete(_file.Path);
        ((IConfigurationRoot)_configuration).Reload();

        _configuration["Smtp:Host"].Should().Be("old.example.com");
        _configuration["Invitations:LifetimeHours"].Should().BeNull("a live setting follows the file");
    }

    /// <summary>
    /// The stored secret is a restart-grade key under its encrypted name, which is the name the file
    /// holds it by.
    /// </summary>
    [Fact]
    public void AStoredSecretIsHeldUnderTheNameItIsStoredBy()
    {
        Start(new JsonObject { ["Smtp"] = new JsonObject { ["ProtectedPassword"] = "old-ciphertext" } });

        Save(new JsonObject { ["Smtp"] = new JsonObject { ["ProtectedPassword"] = "new-ciphertext" } });

        _configuration["Smtp:ProtectedPassword"].Should().Be("old-ciphertext");
    }

    /// <summary>
    /// <b>Held before the reload is announced</b>, so an options monitor recomputing in response
    /// never binds the saved value, not even once.
    /// </summary>
    [Fact]
    public void AListenerToTheReloadAlreadySeesTheHeldValue()
    {
        Start(new JsonObject { ["Smtp"] = new JsonObject { ["Host"] = "old.example.com" } });

        List<string?> seen = [];

        using IDisposable listening = ChangeToken.OnChange(((IConfiguration)_configuration).GetReloadToken, () => seen.Add(_configuration["Smtp:Host"]));

        Save(new JsonObject { ["Smtp"] = new JsonObject { ["Host"] = "new.example.com" } });

        seen.Should().NotBeEmpty().And.OnlyContain(value => value == "old.example.com");
    }

    private void Start(JsonObject contents, IDictionary<string, string?>? beneath = null)
    {
        _file.Write(contents);

        if (beneath is not null)
        {
            _configuration.AddInMemoryCollection(beneath);
        }

        _file.AddTo(_configuration);
    }

    private void Save(JsonObject contents)
    {
        _file.Write(contents);
        ((IConfigurationRoot)_configuration).Reload();
    }

    private string? SavedValue(string key)
    {
        using ConfigurationRoot saved = SettingsFileProvider.Saved(_configuration);

        return saved[key];
    }
}
