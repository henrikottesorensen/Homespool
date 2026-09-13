using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using AwesomeAssertions;

namespace Homespool.Host.Test;

/// <summary>
/// Nothing in the application writes a string as markup. A sentence that carries an element is built
/// with <c>HtmlLocaliser</c> and <c>Markup</c>, which encode every value they are given.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a ban rather than a rule about what may be interpolated.</b> A text scanner can only inspect
/// the shapes it knows, and a string reaches markup in more shapes than it can list: a bare argument to
/// <c>string.Format</c>, a localised string with arguments, a variable handed straight to
/// <c>Html.Raw</c>. A token that is not in the tree cannot be misused, and finding one is something a
/// text scan does reliably.
/// </para>
/// <para>
/// <b>Why the resource half is allowed to be markup.</b> <c>HtmlLocaliser</c> writes the resource as
/// HTML, because that is where a translator puts the sentence; the resources are ours. What would
/// break that is a key that is not ours - a key with no resource is written back as the sentence -
/// so its keys must be literals, and it must be the one declared in <c>_ViewImports</c>.
/// </para>
/// <para>
/// <b>One file may take markup, and only as a type.</b> <c>Markup.Kbd</c> wraps another element, which
/// means appending it as HTML; that is allowed in <c>Pages/Markup.cs</c> for an argument declared as
/// <c>IHtmlContent</c>, and nowhere else. <c>MarkupTests</c> shows every element there encodes its text.
/// </para>
/// </remarks>
public sealed class RawHtmlEncodingTests
{
    private const string MarkupFile = "Homespool.Host/Pages/Markup.cs";
    private const string ViewImports = "Homespool.Host/Pages/_ViewImports.cshtml";

    /// <summary>Each way of writing a string into a page as markup, and what it does.</summary>
    private static readonly (Regex pattern, string reason)[] Sinks =
    [
        (new(@"\bHtml\.Raw\s*\(", RegexOptions.Compiled), "writes its argument as markup"),
        (new(@"(?<![A-Za-z_])HtmlString\b", RegexOptions.Compiled), "wraps a string as markup"),
        (new(@"(?<![A-Za-z_])HtmlFormattableString\b", RegexOptions.Compiled), "writes its format string as markup"),
        (new(@"(?<![A-Za-z_])LocalizedHtmlString\s*\(", RegexOptions.Compiled), "writes its value as markup"),
        (new(@"\bMarkupString\b", RegexOptions.Compiled), "wraps a string as markup"),
        (new(@"\bSetHtmlContent\s*\(", RegexOptions.Compiled), "sets a tag helper's content as markup"),
        (new(@"\bAppendHtml(Line)?\s*\(", RegexOptions.Compiled), "appends markup"),
        (new(@"\bHtmlLocaliser\s*\[(?!\s*"")", RegexOptions.Compiled), "writes a missing key back as markup, so the key must be a literal"),
        (new(@"\b(IHtmlLocalizer|IViewLocalizer)\b(<[^>]*>)?\s+[A-Za-z_]\w*", RegexOptions.Compiled),
         "is an HTML localiser under another name, whose keys nothing checks"),
        (new(@"Service\s*<\s*(IHtmlLocalizer|IViewLocalizer)\b", RegexOptions.Compiled),
         "is an HTML localiser under another name, whose keys nothing checks"),
    ];

    [Fact]
    public void NothingWritesAStringAsMarkup()
    {
        List<string> offences = [];

        foreach (string path in Sources())
        {
            string relative = Relative(path);
            string[] lines = File.ReadAllLines(path);

            for (int index = 0; index < lines.Length; index++)
            {
                foreach ((Regex pattern, string reason) in Sinks)
                {
                    foreach (Match match in pattern.Matches(lines[index]))
                    {
                        if (!IsTheSanctionedUse(relative, lines[index], match))
                        {
                            offences.Add($"{relative}:{index + 1} {match.Value.Trim()} - {reason}");
                        }
                    }
                }
            }
        }

        offences.Should().BeEmpty(
            "a sentence with an element in it is HtmlLocaliser[\"Key\", Markup.Code(value)], which encodes the value; "
            + "anything that writes a string as markup is the mistake that put a printer's firmware string on a page as HTML");
    }

    /// <summary>
    /// The one exemption takes markup only as a type: every <c>AppendHtml</c> in <c>Markup.cs</c> is
    /// handed a parameter declared <c>IHtmlContent</c>, so a string cannot reach it without failing to
    /// compile.
    /// </summary>
    [Fact]
    public void MarkupAppendsHtmlOnlyFromAnHtmlContentParameter()
    {
        string text = File.ReadAllText(Path.Combine(SourceRoot(), MarkupFile));

        List<string> appended = Regex.Matches(text, @"\bAppendHtml\s*\(\s*(\w+)\s*\)").Select(match => match.Groups[1].Value).ToList();

        appended.Should().NotBeEmpty("Markup.Kbd wraps another element, and this is what checks how");
        Regex.Matches(text, @"\bAppendHtml(Line)?\s*\(").Count.Should().Be(appended.Count,
                                                                            "every call appends a bare identifier and nothing else");

        foreach (string parameter in appended.Distinct())
        {
            Regex.Matches(text, $@"(\w+)\s+{Regex.Escape(parameter)}\s*[,)=]")
                 .Select(match => match.Groups[1].Value)
                 .Should().OnlyContain(type => type == "IHtmlContent",
                                       $"'{parameter}' is appended as markup, so it may only ever be declared as IHtmlContent");
        }
    }

    /// <summary>
    /// A ban passes trivially if the scan reads nothing, so this pins what it reads: the views, the
    /// code, and the calls the key rule exists to check.
    /// </summary>
    [Fact]
    public void TheScanStillReadsWhatItIsMeantToCheck()
    {
        List<string> sources = Sources().ToList();

        sources.Count(path => path.EndsWith(".cshtml", StringComparison.Ordinal)).Should().BeGreaterThan(50);
        sources.Count(path => path.EndsWith(".cs", StringComparison.Ordinal)).Should().BeGreaterThan(300);
        sources.Select(Relative).Should().Contain([MarkupFile, ViewImports]);

        sources.Sum(path => Regex.Matches(File.ReadAllText(path), @"\bHtmlLocaliser\s*\[\s*""").Count)
               .Should().BeGreaterThan(30, "the views build this many sentences with elements in them");
    }

    /// <summary>
    /// The exemptions, each in the one file it belongs to: <c>Markup.Kbd</c>'s append, which the test
    /// above constrains, and the <c>HtmlLocaliser</c> declaration itself.
    /// </summary>
    private static bool IsTheSanctionedUse(string relative, string line, Match match)
    {
        if (relative == MarkupFile && match.Value.StartsWith("AppendHtml", StringComparison.Ordinal))
        {
            return true;
        }

        return relative == ViewImports
               && line.TrimStart().StartsWith("@inject ", StringComparison.Ordinal)
               && Regex.IsMatch(match.Value, @"^IHtmlLocalizer<SharedResource>\s+HtmlLocaliser$");
    }

    private static IEnumerable<string> Sources()
    {
        string separator = Path.DirectorySeparatorChar.ToString();

        return Directory.EnumerateFiles(Path.Combine(SourceRoot(), "Homespool.Host"), "*.*", SearchOption.AllDirectories)
                        .Where(path => path.EndsWith(".cshtml", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
                        .Where(path => !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                                       && !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
                        .Order(StringComparer.Ordinal);
    }

    private static string Relative(string path)
    {
        return Path.GetRelativePath(SourceRoot(), path).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string SourceRoot()
    {
        string directory = AppContext.BaseDirectory;

        while (directory is not null && !Directory.Exists(Path.Combine(directory, "Homespool.Host", "Localisation")))
        {
            directory = Path.GetDirectoryName(directory)!;
        }

        directory.Should().NotBeNull("the tests run from inside the repository");

        return directory!;
    }
}
