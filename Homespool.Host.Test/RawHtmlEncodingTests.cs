using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AwesomeAssertions;

namespace Homespool.Host.Test;

/// <summary>
/// What may be interpolated into an <c>Html.Raw</c> argument: a resource string, a generated URL, or
/// something explicitly encoded. Nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the rule needs enforcing at all.</b> Sentences carrying markup are single resource strings
/// with a <c>{0}</c> in them, so that a translator can move the fragment, and the fragment is composed
/// at the call site - which makes <c>Html.Raw</c> correct exactly as long as both halves are ours. A
/// printer's stated firmware version is not ours, and reaches those sentences.
/// </para>
/// <para>
/// <b>So this is a test rather than a written-down conclusion.</b> A claim about every view in the
/// tree stops being true the moment somebody adds a view, and nobody editing one page goes looking
/// for the paragraph that vouched for all of them.
/// </para>
/// <para>
/// <b>Why a URL passes unencoded.</b> <c>Url.Page</c> percent-encodes the route values it is given,
/// so its result cannot carry a <c>&lt;</c> or a quote out of one - it is generated, not passed
/// through. An <c>&amp;</c> between two query values reaches the browser bare, which is untidy rather
/// than dangerous, and encoding it is a separate argument from this one.
/// </para>
/// <para>
/// <b>There is deliberately no exemption list.</b> A hole carrying something operator-configured or
/// generated - a certificate name, the OIDC provider's display name, a TOTP key - is not reachable by
/// an unprivileged caller and is still encoded rather than excused. Exempting one restores the thing
/// this replaces: a promise about a call site, kept by whoever remembers to check it.
/// </para>
/// </remarks>
public sealed class RawHtmlEncodingTests
{
    /// <summary>
    /// The three shapes an interpolation hole may take. A resource string is ours by definition; the
    /// other two say so at the call site.
    /// </summary>
    private static readonly string[] Permitted =
    [
        "Localiser[",
        "Html.Encode(",
        "Url.Page(",
        "Url.Action(",
    ];

    /// <summary>
    /// No view interpolates anything unencoded into markup it then hands to <c>Html.Raw</c>.
    /// </summary>
    [Fact]
    public void EveryInterpolationIntoRawMarkupIsOursOrEncoded()
    {
        List<string> offences = [];

        foreach (string path in Views())
        {
            string text = File.ReadAllText(path);
            string name = Path.GetFileName(path);

            foreach ((int line, string argument) in RawArguments(text))
            {
                foreach (string hole in Holes(argument))
                {
                    if (Permitted.Any(shape => hole.StartsWith(shape, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    offences.Add($"{name}:{line} interpolates {hole}");
                }
            }
        }

        offences.Should().BeEmpty(
            "a value interpolated into an Html.Raw argument is markup, so anything that is not a "
            + "resource string or a generated URL has to go through Html.Encode first");
    }

    /// <summary>
    /// The test above can only bite while it still finds the calls, and a Razor file is scanned as
    /// text rather than parsed - so this pins the number it sees. A drop to zero would leave it
    /// green and blind, which is the failure mode a scanner has and an ordinary test does not.
    /// </summary>
    [Fact]
    public void TheScanStillFindsTheCallsItIsMeantToCheck()
    {
        int holes = Views().Sum(path => RawArguments(File.ReadAllText(path))
                                        .Sum(call => Holes(call.argument).Count));

        holes.Should().BeGreaterThan(20, "the views interpolate this many values into Html.Raw arguments, "
                                         + "and a scan finding none of them would pass while proving nothing");
    }

    private static IEnumerable<string> Views()
    {
        return Directory.EnumerateFiles(Path.Combine(SourceRoot(), "Homespool.Host"), "*.cshtml",
                                        SearchOption.AllDirectories)
                        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                                      StringComparison.Ordinal)
                                       && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                                         StringComparison.Ordinal))
                        .Order(StringComparer.Ordinal);
    }

    /// <summary>
    /// The argument text of every <c>Html.Raw(...)</c> call, with its line number. Parenthesis
    /// depth is counted outside string literals, so a call spanning several lines - which the longer
    /// ones do - comes back whole.
    /// </summary>
    private static List<(int line, string argument)> RawArguments(string text)
    {
        const string opener = "Html.Raw(";

        List<(int, string)> calls = [];

        for (int at = text.IndexOf(opener, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(opener, at + 1, StringComparison.Ordinal))
        {
            int start = at + opener.Length;
            int index = start;
            int depth = 1;
            bool inString = false;
            bool escaped = false;

            while (index < text.Length && depth > 0)
            {
                char c = text[index];

                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = !inString;
                }
                else if (!inString && c == '(')
                {
                    depth++;
                }
                else if (!inString && c == ')')
                {
                    depth--;
                }

                index++;
            }

            calls.Add((text.Take(at).Count(c => c == '\n') + 1, text[start..(index - 1)]));
        }

        return calls;
    }

    /// <summary>
    /// Every <c>{...}</c> hole in every interpolated literal inside one argument. Braces are counted
    /// so a hole containing an object initialiser - a route-value bag - comes back as one hole
    /// rather than being cut in half.
    /// </summary>
    private static List<string> Holes(string argument)
    {
        List<string> holes = [];

        for (int at = argument.IndexOf("$\"", StringComparison.Ordinal); at >= 0;
             at = argument.IndexOf("$\"", at + 1, StringComparison.Ordinal))
        {
            int index = at + 2;
            bool escaped = false;

            while (index < argument.Length)
            {
                char c = argument[index];

                if (escaped)
                {
                    escaped = false;
                    index++;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    index++;
                    continue;
                }

                if (c == '"')
                {
                    break;
                }

                if (c == '{')
                {
                    int depth = 1;
                    int end = index + 1;

                    while (end < argument.Length && depth > 0)
                    {
                        if (argument[end] == '{')
                        {
                            depth++;
                        }
                        else if (argument[end] == '}')
                        {
                            depth--;
                        }

                        end++;
                    }

                    holes.Add(argument[(index + 1)..(end - 1)].Trim());
                    index = end;

                    continue;
                }

                index++;
            }
        }

        return holes;
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
