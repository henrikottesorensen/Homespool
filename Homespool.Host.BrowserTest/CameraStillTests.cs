using System.Threading.Tasks;

using AwesomeAssertions;

using Homespool.Host.E2ETest;

using static Microsoft.Playwright.Assertions;

namespace Homespool.Host.BrowserTest;

/// <summary>
/// The polled still on a printer page, in a real browser - <c>camera.js</c>.
/// </summary>
public sealed class CameraStillTests(Browsers browsers)
{
    /// <summary>
    /// A camera that sends pictures shows one, decoded, with its age beside it and nothing over it.
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
        await Expect(scenario.Caption).Not.ToBeEmptyAsync();
    }

    /// <summary>
    /// A camera that stops answering has its picture taken down and is said to be not answering,
    /// rather than leaving its last frame on screen looking like now.
    /// </summary>
    /// <remarks>
    /// Real time, about twenty seconds of it: the server stops serving the old frame after its
    /// maximum age - four seconds here, which has to outlast the page's two-second poll or no frame is
    /// ever fresh enough to serve - and the page gives a silent camera fifteen before it calls it.
    /// The page's own clock cannot be run forward instead, because a jump longer than a few polls is
    /// read, correctly, as the page having stopped asking rather than the camera having stopped
    /// answering.
    /// </remarks>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task ACameraThatStopsAnsweringIsTakenDown(string engine)
    {
        await using CameraScenario scenario = await CameraScenario.OpenAsync(
            browsers, engine, "still-silent", FakeCamera.Jpeg,
            configure: factory => factory.ConfigurationOverrides["Cameras:MaxAgeSeconds"] = "4");

        await Expect(scenario.Image).ToBeVisibleAsync(new() { Timeout = 15_000 });

        scenario.Host.Sidecar.AddCamera(scenario.Source, FakeCamera.Jpeg with { Producing = false });

        await Expect(scenario.Page.Locator(".camera-status")).ToHaveTextAsync("Camera not answering", new() { Timeout = 40_000 });
        await Expect(scenario.Image).ToBeHiddenAsync();
        (await scenario.Image.GetAttributeAsync("src")).Should().BeNull("an old frame kept anywhere is one that can come back");
        await Expect(scenario.Caption).ToBeEmptyAsync();
    }
}
