using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Playwright;

using Homespool.Host.E2ETest;

using static Microsoft.Playwright.Assertions;

namespace Homespool.Host.BrowserTest;

/// <summary>
/// The polled still on a printer page, in a real browser - <c>camera.js</c>.
/// </summary>
public sealed class CameraStillTests(Browsers browsers)
{
    /// <summary>
    /// A camera that sends pictures shows one, decoded, with its age beside it and nothing over it. A
    /// fresh one is "just now", not "live": the live view's button sits on this picture, and "live" under
    /// it read as though its video were already playing.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task AWorkingCameraShowsAPicture(string engine)
    {
        await using CameraScenario scenario = await CameraScenario.OpenAsync(browsers, engine, "still-working", FakeCamera.Jpeg);

        await Expect(scenario.Image).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(scenario.Image).ToHaveJSPropertyAsync("naturalWidth", 32);
        await Expect(scenario.Page.Locator(".camera-status")).ToBeHiddenAsync();
        await Expect(scenario.Caption).ToHaveTextAsync(await scenario.CaptionLabelAsync("now"));
        await Expect(scenario.Caption).Not.ToHaveTextAsync(
            new Regex($"^{Regex.Escape(await scenario.LabelAsync("live"))}$", RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// A camera with nothing to show yet is said to be capturing, in the page's language - the script
    /// writes it again on every poll that comes back empty, and used to write it in English.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task ACameraWithNothingYetIsCapturingInThePagesLanguage(string engine)
    {
        await using CameraScenario scenario = await CameraScenario.OpenAsync(
            browsers, engine, "still-capturing", FakeCamera.Jpeg with { Producing = false }, locale: "da-DK");

        await Expect(scenario.Page.Locator("html")).ToHaveAttributeAsync("lang", "da");

        // Two empty answers, so the script has certainly finished with the first: the page renders the
        // same words before any poll, and checking only those would prove nothing about the script.
        for (int answered = 0; answered < 2; answered++)
        {
            await scenario.Page.WaitForResponseAsync(
                response => response.Url.EndsWith("/frame", System.StringComparison.Ordinal) && response.Status == 204,
                new() { Timeout = 15_000 });
        }

        await Expect(scenario.Page.Locator(".camera-status")).ToHaveTextAsync(await scenario.StatusLabelAsync("capturing"));
        await Expect(scenario.Image).ToBeHiddenAsync();
    }

    /// <summary>
    /// A picture with no caption beside it keeps refreshing - the front page's drop dialog, whose view
    /// arrives after load. Writing its age to a caption that is not there threw at the end of the first
    /// poll, before the next was scheduled, and that picture never changed again.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task APictureWithNoCaptionKeepsRefreshing(string engine)
    {
        await using CameraScenario scenario = await CameraScenario.OpenAsync(browsers, engine, "still-uncaptioned", FakeCamera.Jpeg);

        // Bound the way the drop dialog's is, by the hook for views that arrive after load, and marked
        // up as that dialog's is: a view and its picture, with nothing beside them.
        await scenario.Page.EvaluateAsync("""
            () => {
                const frame = document.querySelector("[data-camera-frame]").dataset.cameraFrame;
                const host = document.createElement("div");
                host.id = "uncaptioned";
                host.innerHTML =
                    '<div class="camera-view" data-camera-frame="' + frame + '">' +
                    '<img class="camera-image d-none" alt="" /><div class="camera-status"></div></div>';
                document.body.appendChild(host);
                window.homespoolCameras.attachWithin(host);
            }
            """);

        ILocator picture = scenario.Page.Locator("#uncaptioned .camera-image");
        await Expect(picture).ToHaveAttributeAsync("src", new Regex("^blob:"), new() { Timeout = 15_000 });
        string? first = await picture.GetAttributeAsync("src");

        // Every frame fetched is a new blob, so a changed source is a poll that came back.
        await Expect(picture).Not.ToHaveAttributeAsync("src", first!, new() { Timeout = 10_000 });
    }

    /// <summary>
    /// A camera that stops answering has its picture taken down and is said to be not answering,
    /// rather than leaving its last frame on screen looking like now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In Danish, so that every word checked here proves it came from the page: in English the page's
    /// wording and a sentence written into the script are the same string.
    /// </para>
    /// <para>
    /// Real time, about twenty seconds of it: the server stops serving the old frame after its
    /// maximum age - four seconds here, which has to outlast the page's two-second poll or no frame is
    /// ever fresh enough to serve - and the page gives a silent camera fifteen before it calls it.
    /// The page's own clock cannot be run forward instead, because a jump longer than a few polls is
    /// read, correctly, as the page having stopped asking rather than the camera having stopped
    /// answering.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task ACameraThatStopsAnsweringIsTakenDown(string engine)
    {
        await using CameraScenario scenario = await CameraScenario.OpenAsync(
            browsers, engine, "still-silent", FakeCamera.Jpeg,
            configure: factory => factory.ConfigurationOverrides["Cameras:MaxAgeSeconds"] = "4",
            locale: "da-DK");

        await Expect(scenario.Page.Locator("html")).ToHaveAttributeAsync("lang", "da");
        await Expect(scenario.Image).ToBeVisibleAsync(new() { Timeout = 15_000 });

        scenario.Host.Sidecar.AddCamera(scenario.Source, FakeCamera.Jpeg with { Producing = false });

        // Aged in the page's own words on the way down.
        string secondsAgo = Regex.Escape(await scenario.CaptionLabelAsync("seconds-ago"))
                                 .Replace(@"\{0}", @"\d+", System.StringComparison.Ordinal);
        await Expect(scenario.Caption).ToHaveTextAsync(new Regex($"^{secondsAgo}$"), new() { Timeout = 20_000 });

        await Expect(scenario.Page.Locator(".camera-status")).ToHaveTextAsync(
            await scenario.StatusLabelAsync("not-answering"), new() { Timeout = 40_000 });
        await Expect(scenario.Image).ToBeHiddenAsync();
        (await scenario.Image.GetAttributeAsync("src")).Should().BeNull("an old frame kept anywhere is one that can come back");
        await Expect(scenario.Caption).ToBeEmptyAsync();
    }
}
