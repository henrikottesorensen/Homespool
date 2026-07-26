using System.Text.RegularExpressions;

using AwesomeAssertions;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Scrapes the antiforgery hidden field out of a rendered Razor Page's HTML, for tests that drive a
/// real POST through <c>WebApplicationFactory</c> rather than bypassing antiforgery validation.
/// Shared by every such test (<see cref="SetupFlowTests"/>, <see cref="LoginFlowTests"/>) so the
/// scraping regex exists once.
/// </summary>
public static class AntiforgeryTestHelper
{
    public static string ExtractToken(string html)
    {
        Match inputTag = Regex.Match(html, """<input[^>]*name="__RequestVerificationToken"[^>]*>""");
        inputTag.Success.Should().BeTrue("the page must render the antiforgery hidden field");

        Match value = Regex.Match(inputTag.Value, "value=\"([^\"]+)\"");
        value.Success.Should().BeTrue("the antiforgery input must carry a value");

        return value.Groups[1].Value;
    }
}
