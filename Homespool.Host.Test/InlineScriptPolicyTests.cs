using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using AwesomeAssertions;

namespace Homespool.Host.Test;

/// <summary>
/// Every script a page runs comes from a file or carries the response's nonce, and no element carries
/// an inline handler - the two rules the Content-Security-Policy enforces in the browser, checked
/// here in the source so a new view fails the build rather than the policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>The policy is only as good as the views' discipline</b>, and the browser's refusal is the
/// wrong place to learn about it: a page whose new inline script is silently not run looks like a
/// feature that does nothing. The scan is deliberately blunt - a <c>&lt;script&gt;</c> tag with no
/// <c>src</c> must carry <c>nonce=</c>, and no tag may carry an <c>on…=</c> attribute - because the
/// policy is blunt too, and a scan that understood exceptions would be one that let some through.
/// </para>
/// <para>
/// The <c>on…=</c> rule is matched on an attribute at the start of a tag's attribute list or after
/// whitespace, so prose containing the word <c>on</c> followed by an equals sign in a Razor
/// expression does not trip it, and <c>@onclick</c>-style Blazor syntax would, which is correct: it
/// is not used here.
/// </para>
/// </remarks>
public sealed class InlineScriptPolicyTests
{
    private static readonly Regex ScriptTag = new(@"<script\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InlineHandler = new(@"<[a-zA-Z][^>]*\son[a-z]+\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void EveryInlineScriptCarriesTheNonce()
    {
        List<string> offences = [];
        int inline = 0;

        foreach (string view in Views())
        {
            string text = File.ReadAllText(view);

            foreach (Match tag in ScriptTag.Matches(text))
            {
                if (tag.Value.Contains("src=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                inline++;

                if (!tag.Value.Contains("nonce=", StringComparison.OrdinalIgnoreCase))
                {
                    offences.Add($"{Relative(view)}: {tag.Value}");
                }
            }
        }

        offences.Should().BeEmpty("an inline script without the nonce is one the browser refuses to run");
        inline.Should().BeGreaterThan(0, "the colour-mode block in _Layout is inline on purpose, so a scan finding no inline script has lost it");
    }

    [Fact]
    public void NoViewCarriesAnInlineHandler()
    {
        List<string> offences = [];

        foreach (string view in Views())
        {
            string text = File.ReadAllText(view);

            foreach (Match tag in InlineHandler.Matches(text))
            {
                offences.Add($"{Relative(view)}: {tag.Value}");
            }
        }

        offences.Should().BeEmpty("an inline handler is script the policy refuses; put it in site.js behind a data- attribute");
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

    private static string Relative(string path)
    {
        return Path.GetRelativePath(SourceRoot(), path);
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
