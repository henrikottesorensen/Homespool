using System;

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
    [Fact]
    public void FallsBackToTheReportedModel()
    {
        Printer printer = new() { Model = "COREONE", Uuid = Guid.NewGuid() };

        PrinterDisplayName.For(printer).Should().Be("COREONE");
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
}
