using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using AwesomeAssertions;

using Homespool.Host.Pages;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The fallback chain for naming a printer that may never have been named.
/// </summary>
/// <remarks>
/// Extracted from the listing view when the front page needed the same answer. These pin the order,
/// which is the part that would rot silently: a second copy adding a link in one place only is
/// exactly the drift the shared helper exists to prevent.
/// </remarks>
public class PrinterDisplayNameTests
{
    /// <summary>A name somebody chose wins over everything else.</summary>
    [Fact]
    public void PrefersTheNameSomebodyGaveIt()
    {
        Printer printer = new() { Name = "Workshop", Model = "COREONE", Uuid = Guid.NewGuid() };

        PrinterDisplayName.For(printer).Should().Be("Workshop");
    }

    /// <summary>With no name, the model the printer reported is better than its uuid.</summary>
    /// <remarks>
    /// <b>Reported, not stored raw.</b> What arrives on the wire is <c>printer_type</c> - a version
    /// triple - so without resolving it an unnamed printer read out as <c>7.1.0</c>: a string that
    /// looks like it means something to the person reading it, and does not.
    /// </remarks>
    [Fact]
    public void FallsBackToTheReportedModel()
    {
        Printer printer = new() { Model = "7.1.0", Uuid = Guid.NewGuid() };

        PrinterDisplayName.For(printer).Should().Be("COREONE");
    }

    /// <summary>
    /// An unnamed XL+ is called what it calls itself on its own screen, not by firmware's id for it.
    /// </summary>
    [Fact]
    public void AnUnnamedXlPlusIsCalledXlPlus()
    {
        Printer printer = new() { Model = "3.1.1", Uuid = Guid.NewGuid() };

        PrinterDisplayName.For(printer).Should().Be("XL+");
    }

    /// <summary>
    /// A triple newer than the names table is still better than the uuid: it is the most specific
    /// true thing there is to say about the machine.
    /// </summary>
    [Fact]
    public void KeepsAnUnresolvableModelRatherThanFallingPastIt()
    {
        Printer printer = new() { Model = "9.9.9", Uuid = Guid.NewGuid() };

        PrinterDisplayName.For(printer).Should().Be("9.9.9");
    }

    /// <summary>With neither, the uuid - which looks like a prompt to go and name the thing.</summary>
    [Fact]
    public void FallsBackToTheUuid()
    {
        Guid uuid = Guid.NewGuid();
        Printer printer = new() { Uuid = uuid };

        PrinterDisplayName.For(printer).Should().Be(uuid.ToString());
    }

    /// <summary>
    /// Whitespace is not a name. A printer called " " would otherwise render as an empty tile, which
    /// is worse than the uuid it was hiding.
    /// </summary>
    [Fact]
    public void TreatsWhitespaceAsUnnamed()
    {
        Printer printer = new() { Name = "   ", Model = "MK4S", Uuid = Guid.NewGuid() };

        PrinterDisplayName.For(printer).Should().Be("MK4S");
    }

    /// <summary>
    /// No page or service builds the chain itself - every one of them asks this helper.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The chain is short enough to retype, which is the danger.</b> An inline
    /// <c>Name ?? Model ?? Uuid</c> reads the reported model as though it were a name, so it shows an
    /// unnamed printer as <c>1.3.5</c> - and a copy in a removal confirmation has to agree with the one
    /// that checks it to the character, which two copies only do by luck.
    /// </para>
    /// <para>
    /// <b>Scoped to the shape the copies take</b>: <c>.Model ??</c>, in product code. It does not try
    /// to recognise every way a name could be assembled; it stops the one that keeps coming back.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoPageOrServiceBuildsTheChainItself()
    {
        DirectoryInfo host = new(Path.Combine(RepositoryRoot().FullName, "Homespool.Host"));
        Regex inlineChain = new(@"\.Model\s*\?\?");

        string[] offenders = host.EnumerateFiles("*", SearchOption.AllDirectories)
                                 .Where(file => file.Extension is ".cs" or ".cshtml")
                                 .Where(file => !file.FullName.Split(Path.DirectorySeparatorChar)
                                                     .Any(part => part is "obj" or "bin"))
                                 .SelectMany(file => File.ReadLines(file.FullName)
                                                         .Select((line, index) => (file, line, index)))
                                 .Where(hit => inlineChain.IsMatch(hit.line))
                                 .Select(hit => $"{Path.GetRelativePath(host.FullName, hit.file.FullName)}:{hit.index + 1}")
                                 .ToArray();

        offenders.Should().BeEmpty("a printer's display name comes from PrinterDisplayName.For, which resolves the model");
    }

    private static DirectoryInfo RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Homespool.slnx")))
        {
            directory = directory.Parent;
        }

        return directory ??
               throw new InvalidOperationException($"No Homespool.slnx above {AppContext.BaseDirectory}.");
    }
}
