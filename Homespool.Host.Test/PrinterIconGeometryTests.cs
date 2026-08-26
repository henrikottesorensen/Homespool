using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using AwesomeAssertions;

namespace Homespool.Host.Test;

/// <summary>
/// The inlined printer drawings still match the files they were copied from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Because inlining makes a copy, and a copy drifts.</b> <c>_PrinterIcon.cshtml</c> carries the
/// geometry of <c>printer-bedslinger.svg</c> and <c>printer-enclosed.svg</c> verbatim - inlined
/// rather than referenced so <c>currentColor</c> resolves against the page, which is what lets the
/// tile lift the brand blue on a dark ground. The cost is that editing the files changes nothing on
/// the page, silently.
/// </para>
/// <para>
/// <b>Written after exactly that happened</b> (2026-08-23): the drawings were redrawn thinner, the
/// files changed, and the page went on rendering the heavy version because nothing connected the two.
/// A comment saying "keep these in step" had been sitting in the partial the whole time.
/// </para>
/// <para>
/// Three attributes are deliberately dropped on the way in and are not compared: <c>color</c>, which
/// would pin the blue and defeat the inheritance; and <c>role</c> with its <c>aria-label</c>, because
/// the plaque under the drawing already names the printer. Everything that draws is compared.
/// </para>
/// </remarks>
public class PrinterIconGeometryTests
{
    [Theory]
    [InlineData("printer-enclosed.svg", "printer-icon-enclosed")]
    [InlineData("printer-bedslinger.svg", "printer-icon-open")]
    public void TheInlinedDrawingMatchesItsFile(string fileName, string cssClass)
    {
        // Arrange
        string root = SourceRoot();
        string file = File.ReadAllText(Path.Combine(root, "Homespool.Host", "wwwroot", "img", fileName));
        string partial = File.ReadAllText(
            Path.Combine(root, "Homespool.Host", "Pages", "Shared", "_PrinterIcon.cshtml"));

        // Act
        IReadOnlyList<string> drawn = Shapes(file);
        IReadOnlyList<string> inlined = Shapes(BranchFor(partial, cssClass));

        // Assert
        inlined.Should().Equal(drawn,
                               "the inlined copy of {0} has drifted from the file - re-copy it, or the "
                               + "page renders a drawing nobody is looking at any more", fileName);
    }

    /// <summary>
    /// The stroke weight on the root element, which is not a shape and so is not compared above.
    /// </summary>
    /// <remarks>
    /// Its own test because it is the attribute that actually changed when this drift happened: the
    /// geometry was identical and every line was a different thickness.
    /// </remarks>
    [Theory]
    [InlineData("printer-enclosed.svg", "printer-icon-enclosed")]
    [InlineData("printer-bedslinger.svg", "printer-icon-open")]
    public void TheInlinedDrawingCarriesTheSameRootStrokeWidth(string fileName, string cssClass)
    {
        string root = SourceRoot();
        string file = File.ReadAllText(Path.Combine(root, "Homespool.Host", "wwwroot", "img", fileName));
        string partial = File.ReadAllText(
            Path.Combine(root, "Homespool.Host", "Pages", "Shared", "_PrinterIcon.cshtml"));

        RootStrokeWidth(BranchFor(partial, cssClass)).Should().Be(RootStrokeWidth(file));
    }

    /// <summary>Every shape in a drawing, as a normalised attribute list, in document order.</summary>
    private static IReadOnlyList<string> Shapes(string markup)
    {
        return [.. Regex.Matches(markup, @"<(rect|path|circle)\b([^>]*?)/?>", RegexOptions.Singleline)
                        .Select(shape => shape.Groups[1].Value + " " + Normalise(shape.Groups[2].Value))];
    }

    /// <summary>
    /// The one branch of the partial that draws this form factor.
    /// </summary>
    /// <remarks>
    /// Sliced on the class rather than parsed: the file is Razor, not XML, and its two drawings are in
    /// an if/else that no XML reader will accept.
    /// </remarks>
    private static string BranchFor(string partial, string cssClass)
    {
        int marker = partial.IndexOf(cssClass, StringComparison.Ordinal);
        marker.Should().BeGreaterThan(-1, "the partial must still carry a drawing classed {0}", cssClass);

        // Back to the element's own start: the class attribute sits after stroke-width, so slicing
        // from the class would hand RootStrokeWidth the first inner weight instead of the root's.
        int start = partial.LastIndexOf("<svg", marker, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "that class must be on an svg element");

        int end = partial.IndexOf("</svg>", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(-1, "that drawing must be a complete element");

        return partial[start..end];
    }

    private static string RootStrokeWidth(string markup)
    {
        Match match = Regex.Match(markup, @"stroke-width=""([0-9.]+)""");
        match.Success.Should().BeTrue("a drawing must state its stroke weight");

        return match.Groups[1].Value;
    }

    /// <summary>Attribute text with whitespace collapsed and self-closing slashes gone.</summary>
    private static string Normalise(string attributes)
    {
        return Regex.Replace(attributes.Replace("/", " ", StringComparison.Ordinal), @"\s+", " ").Trim();
    }

    /// <summary>The repository root, walked up from the test binary - as LocalisationTests does.</summary>
    private static string SourceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Homespool.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests read the source tree, so they have to find it");

        return directory!.FullName;
    }
}
