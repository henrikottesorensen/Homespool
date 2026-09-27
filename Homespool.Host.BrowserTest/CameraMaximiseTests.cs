using System;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Homespool.Host.E2ETest;

using static Microsoft.Playwright.Assertions;

namespace Homespool.Host.BrowserTest;

/// <summary>
/// Maximising a camera panel, in a real browser - <c>camera-maximise.js</c>.
/// </summary>
/// <remarks>
/// <b>The picture must not move.</b> Reparenting an element restarts its load, which for a live MJPEG
/// stream means dropping it and making the sidecar open the camera again - so the test watches the
/// element's identity and the sidecar's count of streams asked for, not only the class.
/// </remarks>
public sealed class CameraMaximiseTests(Browsers browsers)
{
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task MaximisingALiveViewKeepsItsStream(string engine)
    {
        await using CameraScenario scenario = await CameraScenario.OpenAsync(
            browsers, engine, "maximise", FakeCamera.Jpeg);
        await scenario.WatchLiveAsync();

        // A mark on the element itself: a replacement, however identical, would not carry it.
        await scenario.Image.EvaluateAsync("image => image.dataset.original = 'yes'");
        int streamsAsked = StreamsAsked(scenario);

        await scenario.Page.Locator(".camera-maximise").ClickAsync();

        await Expect(scenario.Page.Locator(".camera-view")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("camera-view-maximised"));
        await Expect(scenario.Page.Locator("body")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("camera-maximised-open"));
        await Expect(scenario.Image).ToHaveAttributeAsync("data-original", "yes");
        await Expect(scenario.Page.Locator(".camera-view-maximised .camera-image")).ToHaveCountAsync(1);
        await Expect(scenario.Caption).ToHaveTextAsync(await scenario.LabelAsync("live"));

        await scenario.Page.Keyboard.PressAsync("Escape");

        await Expect(scenario.Page.Locator(".camera-view")).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("camera-view-maximised"));
        StreamsAsked(scenario).Should().Be(streamsAsked, "neither opening nor closing may restart the stream");
        scenario.Host.Sidecar.OpenMjpegStreams.Should().Be(1);
    }

    private static int StreamsAsked(CameraScenario scenario)
    {
        return scenario.Host.Sidecar.Requests.Count(request => request.StartsWith("GET /api/stream.mjpeg", StringComparison.Ordinal));
    }
}
