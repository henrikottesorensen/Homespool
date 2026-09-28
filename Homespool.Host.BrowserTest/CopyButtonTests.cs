using System.Threading.Tasks;

using Microsoft.Playwright;

using Homespool.Host.E2ETest;

using static Microsoft.Playwright.Assertions;

namespace Homespool.Host.BrowserTest;

/// <summary>
/// The print host address's copy button on a printer page - <c>site.js</c> - in the page's language.
/// </summary>
/// <remarks>
/// <para>
/// <b>In Danish</b>, because in English the page's wording and a sentence written into the script are
/// the same string, and a test could not tell which one it saw.
/// </para>
/// <para>
/// <b>The clipboard is stood in for</b> by a script in place before the page's own: whether a real
/// clipboard write succeeds depends on the engine, its permissions and whether the window has focus,
/// none of which is what is being tested. The printer page comes with a camera because that is the
/// page the scenario builds; the camera plays no part.
/// </para>
/// </remarks>
public sealed class CopyButtonTests(Browsers browsers)
{
    /// <summary>
    /// A copy that works says so on the button and to a screen reader, in the page's words.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task ACopiedAddressIsSaidSoInThePagesLanguage(string engine)
    {
        await using CameraScenario scenario = await OpenAsync(engine, "copy-done", "Promise.resolve()");
        ILocator group = scenario.Page.Locator("[data-copy]");

        await group.Locator("[data-copy-button]").ClickAsync();

        await Expect(group.Locator("[data-copy-button]")).ToHaveTextAsync(await LabelAsync(group, "copied"));
        await Expect(scenario.Page.Locator("[data-copy-status]")).ToHaveTextAsync(await LabelAsync(group, "copied-status"));
    }

    /// <summary>
    /// A clipboard that refuses leaves the address selected and says how to copy it by hand, in the
    /// page's words and naming the one key this platform copies with.
    /// </summary>
    /// <remarks>
    /// The platform is stood in for too, so the answer does not depend on the machine running the test:
    /// Chromium is asked through <c>navigator.userAgentData</c>, which it has and WebKit does not, and
    /// WebKit through <c>navigator.platform</c>, which is all it has.
    /// </remarks>
    [Theory]
    [InlineData(Browsers.Chromium, "({ platform: \"macOS\" })", "Win32", "selected-mac")]
    [InlineData(Browsers.Chromium, "({ platform: \"Windows\" })", "MacIntel", "selected")]
    [InlineData(Browsers.WebKit, "undefined", "MacIntel", "selected-mac")]
    [InlineData(Browsers.WebKit, "undefined", "Win32", "selected")]
    public async Task ARefusedCopySaysHowInThePagesLanguage(string engine, string hints, string platform, string label)
    {
        await using CameraScenario scenario = await OpenAsync(
            engine, "copy-refused", "Promise.reject(new DOMException(\"Write permission denied.\", \"NotAllowedError\"))",
            $$"""
                Object.defineProperty(Navigator.prototype, "userAgentData", { configurable: true, get: function () { return {{hints}}; } });
                Object.defineProperty(Navigator.prototype, "platform", { configurable: true, get: function () { return "{{platform}}"; } });
                """);
        ILocator group = scenario.Page.Locator("[data-copy]");

        await group.Locator("[data-copy-button]").ClickAsync();

        await Expect(scenario.Page.Locator("[data-copy-status]")).ToHaveTextAsync(await LabelAsync(group, label));
    }

    private async Task<CameraScenario> OpenAsync(string engine, string name, string writeText, string platform = "")
    {
        CameraScenario scenario = await CameraScenario.OpenAsync(
            browsers, engine, name, FakeCamera.Jpeg,
            beforePage: context => context.AddInitScriptAsync($$"""
                Object.defineProperty(navigator, "clipboard", {
                    configurable: true,
                    value: { writeText: function () { return {{writeText}}; } }
                });
                {{platform}}
                """),
            locale: "da-DK");

        try
        {
            await Expect(scenario.Page.Locator("html")).ToHaveAttributeAsync("lang", "da");
        }
        catch
        {
            await scenario.DisposeAsync();
            throw;
        }

        return scenario;
    }

    private static async Task<string> LabelAsync(ILocator group, string key)
    {
        return await group.GetAttributeAsync($"data-label-{key}") ??
               throw new System.InvalidOperationException($"The copy group carries no {key} label.");
    }
}
