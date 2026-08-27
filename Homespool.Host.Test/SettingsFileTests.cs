using System;
using System.IO;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;

using Homespool.Host.Configuration;

namespace Homespool.Host.Test;

/// <summary>
/// The writable configuration file: where it resolves to, what it does with a file it cannot read,
/// and that a write is never observable half-done.
/// </summary>
public class SettingsFileTests : IDisposable
{
    private readonly string _directory;

    public SettingsFileTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "homespool-settings-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AnUnsetPathResolvesUnderTheContentRoot()
    {
        SettingsFile.Resolve(null, "/app")
                    .Should()
                    .Be(Path.Combine("/app", SettingsFile.DefaultRelativePath));
    }

    [Fact]
    public void AnEmptyPathIsTreatedAsUnset()
    {
        SettingsFile.Resolve("   ", "/app")
                    .Should()
                    .Be(Path.Combine("/app", SettingsFile.DefaultRelativePath));
    }

    [Fact]
    public void ARelativePathIsResolvedAgainstTheContentRoot()
    {
        SettingsFile.Resolve("conf/custom.json", "/app")
                    .Should()
                    .Be(Path.Combine("/app", "conf/custom.json"));
    }

    [Fact]
    public void AnAbsolutePathIsTakenAsGiven()
    {
        string absolute = Path.Combine(Path.GetTempPath(), "elsewhere.json");

        SettingsFile.Resolve(absolute, "/app").Should().Be(absolute);
    }

    [Fact]
    public void AMissingFileReadsAsEmptyRatherThanThrowing()
    {
        File(out SettingsFile file);

        file.Exists.Should().BeFalse();
        file.Read().Should().BeEmpty();
    }

    /// <summary>
    /// A hand-edited file with one brace wrong must not stop the deployment starting. There is no
    /// interface at that point to explain the fault, and no way in to fix it - so the cost of a bad
    /// file is the settings falling back to their configured defaults, which is visible on the page.
    /// </summary>
    [Fact]
    public void AMalformedFileReadsAsEmptyRatherThanThrowing()
    {
        File(out SettingsFile file);

        System.IO.File.WriteAllText(file.Path, "{ \"Smtp\": { ");

        file.Read().Should().BeEmpty();
    }

    [Fact]
    public void AFileHoldingSomethingOtherThanAnObjectReadsAsEmpty()
    {
        File(out SettingsFile file);

        System.IO.File.WriteAllText(file.Path, "[1, 2, 3]");

        file.Read().Should().BeEmpty();
    }

    [Fact]
    public void WhatIsWrittenIsWhatIsRead()
    {
        File(out SettingsFile file);

        JsonObject written = new()
        {
            ["Smtp"] = new JsonObject { ["Host"] = "mail.example.com", ["Port"] = 587 },
        };

        file.Write(written);

        JsonObject read = file.Read();

        read["Smtp"]!["Host"]!.GetValue<string>().Should().Be("mail.example.com");
        read["Smtp"]!["Port"]!.GetValue<int>().Should().Be(587);
    }

    [Fact]
    public void WritingCreatesTheDirectory()
    {
        SettingsFile file = new(Path.Combine(_directory, "nested", "deeper", "settings.json"));

        file.Write(new JsonObject { ["Security"] = new JsonObject { ["RequireTwoFactor"] = true } });

        file.Exists.Should().BeTrue();
    }

    /// <summary>
    /// The temporary file is the mechanism, not an artefact: it is renamed over the target, so it
    /// must never be left behind for the next write to trip over.
    /// </summary>
    [Fact]
    public void NoTemporaryFileSurvivesAWrite()
    {
        File(out SettingsFile file);

        file.Write(new JsonObject { ["Security"] = new JsonObject { ["RequireTwoFactor"] = true } });

        Directory.GetFiles(_directory).Should().ContainSingle().Which.Should().Be(file.Path);
    }

    [Fact]
    public void WritingReplacesRatherThanMerges()
    {
        File(out SettingsFile file);

        file.Write(new JsonObject { ["Smtp"] = new JsonObject { ["Host"] = "first" } });
        file.Write(new JsonObject { ["Smtp"] = new JsonObject { ["Host"] = "second" } });

        file.Read()["Smtp"]!["Host"]!.GetValue<string>().Should().Be("second");
    }

    /// <summary>
    /// The layer binds without the file being there, and without its directory being there either -
    /// which is the state every first start is in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="ConfigurationManager"/> is what <c>WebApplicationBuilder</c> carries, and it
    /// applies a source when the source is added rather than at <c>Build</c>. A plain
    /// <c>ConfigurationBuilder</c> defers everything and would pass this whether or not the real thing
    /// does, so it is the manager that has to be asserted against.
    /// </para>
    /// <para>
    /// <b>What guards the reload decision is not this test.</b> Watching costs an inotify instance per
    /// provider against a default ceiling of 128, and the end-to-end suite starts a host per test
    /// class against one shared content root - with the watcher on, 167 of its 287 tests could not
    /// build a host at all. That suite is the check; nothing at this scale can see it.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheLayerBindsWithNeitherTheFileNorItsDirectoryPresent()
    {
        SettingsFile file = new(Path.Combine(_directory, "absent", "settings.json"));

        Action addingTheLayer = () =>
        {
            using ConfigurationManager configuration = new();

            configuration.AddJsonFile(file.Path, optional: true, reloadOnChange: false);

            configuration["Smtp:Host"].Should().BeNull();
        };

        addingTheLayer.Should().NotThrow();
    }

    /// <summary>
    /// A saved value is visible after an explicit reload, which is what replaces the file watcher.
    /// </summary>
    [Fact]
    public void AReloadPicksUpWhatWasWritten()
    {
        File(out SettingsFile file);

        using ConfigurationManager configuration = new();

        configuration.AddJsonFile(file.Path, optional: true, reloadOnChange: false);

        configuration["Smtp:Host"].Should().BeNull();

        file.Write(new JsonObject { ["Smtp"] = new JsonObject { ["Host"] = "mail.example.com" } });

        ((IConfigurationRoot)configuration).Reload();

        configuration["Smtp:Host"].Should().Be("mail.example.com");
    }

    [Fact]
    public void TheFileIsReadableOnlyByItsOwner()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File(out SettingsFile file);

        file.Write(new JsonObject { ["Smtp"] = new JsonObject { ["Password"] = "ciphertext" } });

        System.IO.File.GetUnixFileMode(file.Path)
                      .Should()
                      .Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
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

    private void File(out SettingsFile file)
    {
        file = new SettingsFile(Path.Combine(_directory, "settings.json"));
    }
}
