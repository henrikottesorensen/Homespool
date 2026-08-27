using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;

using Homespool.Host.Configuration;
using Homespool.Host.Mail;

namespace Homespool.Host.Test;

/// <summary>
/// The one-shot that carries an operator's environment into the settings file: what it takes, what
/// it deliberately leaves behind, and what it refuses.
/// </summary>
public class SettingsWriterTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public SettingsWriterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "homespool-writer-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_directory);

        _path = Path.Combine(_directory, "settings.json");
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AnEmptyEnvironmentWritesNoFileAtAll()
    {
        SettingsWriter.WriteFrom(Configuration([]), _path).Should().Be(0);

        File.Exists(_path).Should().BeFalse("a file of nothing would only freeze defaults");
    }

    /// <summary>
    /// The property this exists to protect: a key absent from the file means "whatever the code
    /// says", so a default must never be written just because it is the current value.
    /// </summary>
    [Fact]
    public void OnlyKeysThatAreSetAreWritten()
    {
        SettingsWriter.WriteFrom(
            Configuration(new() { ["Smtp:Host"] = "mail.example.com" }),
            _path);

        JsonObject written = new SettingsFile(_path).Read();

        written["Smtp"]!["Host"]!.GetValue<string>().Should().Be("mail.example.com");
        written["Smtp"]!.AsObject().Should().NotContainKey("Port", "nothing set a port");
        written.Should().NotContainKey("Cameras");
    }

    [Fact]
    public void AKeyThatIsNotEditableIsIgnored()
    {
        SettingsWriter.WriteFrom(
            Configuration(new()
            {
                ["Smtp:Host"] = "mail.example.com",
                ["Listeners:UserPort"] = "9999",
                ["Certificates:Directory"] = "/somewhere",
            }),
            _path);

        JsonObject written = new SettingsFile(_path).Read();

        written.Should().NotContainKey("Listeners");
        written.Should().NotContainKey("Certificates");
    }

    /// <summary>
    /// The file is meant to be read and hand-edited, so a port is a number rather than a quoted
    /// string. Configuration binding would accept either.
    /// </summary>
    [Fact]
    public void ValuesAreWrittenAsTheTypeThePropertyDeclares()
    {
        SettingsWriter.WriteFrom(
            Configuration(new()
            {
                ["Smtp:Port"] = "465",
                ["Smtp:UseImplicitTls"] = "true",
                ["Smtp:FromName"] = "Homespool",
                ["Storage:MinimumSampleIntervalSeconds"] = "1.5",
            }),
            _path);

        JsonObject written = new SettingsFile(_path).Read();

        written["Smtp"]!["Port"]!.GetValue<int>().Should().Be(465);
        written["Smtp"]!["UseImplicitTls"]!.GetValue<bool>().Should().BeTrue();
        written["Smtp"]!["FromName"]!.GetValue<string>().Should().Be("Homespool");
        written["Storage"]!["MinimumSampleIntervalSeconds"]!.GetValue<double>().Should().Be(1.5);
    }

    /// <summary>
    /// A value that will not parse is kept as the string it was, so a typo survives the migration
    /// visibly and fails validation on the next start rather than disappearing during it.
    /// </summary>
    [Fact]
    public void AnUnparseableValueIsKeptRatherThanDropped()
    {
        SettingsWriter.WriteFrom(Configuration(new() { ["Smtp:Port"] = "not-a-port" }), _path);

        new SettingsFile(_path).Read()["Smtp"]!["Port"]!.GetValue<string>().Should().Be("not-a-port");
    }

    [Fact]
    public void WhatIsWrittenBindsBackToTheOptionsClass()
    {
        SettingsWriter.WriteFrom(
            Configuration(new()
            {
                ["Smtp:Host"] = "mail.example.com",
                ["Smtp:Port"] = "465",
                ["Smtp:UseImplicitTls"] = "true",
            }),
            _path);

        using ConfigurationManager configuration = new();

        configuration.AddJsonFile(_path, optional: false, reloadOnChange: false);

        SmtpOptions options = new();

        configuration.GetSection(SmtpOptions.SectionName).Bind(options);

        options.Host.Should().Be("mail.example.com");
        options.Port.Should().Be(465);
        options.UseImplicitTls.Should().BeTrue();
        options.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void AnExistingFileIsRefusedRatherThanOverwritten()
    {
        File.WriteAllText(_path, "{ \"Smtp\": { \"Host\": \"already.here\" } }");

        SettingsWriter.WriteFrom(Configuration(new() { ["Smtp:Host"] = "new.example.com" }), _path)
                      .Should()
                      .Be(1);

        new SettingsFile(_path).Read()["Smtp"]!["Host"]!.GetValue<string>().Should().Be("already.here");
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

    private static IConfiguration Configuration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
