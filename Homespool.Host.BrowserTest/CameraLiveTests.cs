using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Homespool.Host.E2ETest;

using static Microsoft.Playwright.Assertions;

namespace Homespool.Host.BrowserTest;

/// <summary>
/// Live view on a printer page, in a real browser - <c>camera-live.js</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The MJPEG cases play for real</b>: the fake sidecar streams real frames, tables and all, as
/// Homespool's own sidecar image sends them, and the picture only reports a size once the browser has
/// decoded one.
/// </para>
/// <para>
/// <b>The WebRTC cases cannot play</b> - the fake answers an offer but carries no media - so what they
/// pin is that every way an attempt can fail returns the panel to the still and says so, which is the
/// half of the feature a person notices when it goes wrong.
/// </para>
/// <para>
/// <b>WebKit keeps a multipart picture's connection open after its source is taken away</b> -
/// measured: removing or replacing the <c>src</c>, or the element itself, leaves the stream
/// running for as long as it was watched, and only <c>window.stop()</c> closes it. So the sidecar
/// is not released when a WebKit viewer stops watching, and that is asserted for Chromium alone
/// until the page ends a stream some other way. What the page shows is asserted in both.
/// </para>
/// </remarks>
public sealed class CameraLiveTests(Browsers browsers)
{
    /// <summary>
    /// A JPEG camera plays in the picture's own element, relayed from the sidecar, and the caption
    /// says it is live only once a frame has decoded.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task AJpegCameraPlaysLiveInThePicture(string engine)
    {
        await using CameraScenario scenario = await OpenJpegAsync(engine, "live-plays");

        await scenario.WatchLiveAsync();

        await Expect(scenario.Image).ToHaveJSPropertyAsync("naturalWidth", 32);
        (await scenario.Image.GetAttributeAsync("src")).Should().Contain("/stream.mjpeg");
        await Expect(scenario.LiveToggle).ToHaveAttributeAsync("aria-label", await scenario.LabelAsync("stop"));
        scenario.Host.Sidecar.OpenMjpegStreams.Should().Be(1);
    }

    /// <summary>
    /// A stream that arrives but never decodes is never called live: the caption waits for a picture,
    /// and the page's deadline sends the panel back to the still.
    /// </summary>
    /// <remarks>
    /// "Live" means a frame has decoded - the one thing the page can see of a multipart picture - and
    /// not that bytes are arriving or that the server said yes. The deadline is ten seconds, so the
    /// page's clock is run forward past it.
    /// </remarks>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task AStreamThatNeverDecodesIsNotCalledLive(string engine)
    {
        await using CameraScenario scenario = await CameraScenario.OpenAsync(
            browsers, engine, "live-garbled", FakeCamera.Jpeg,
            beforePage: context => context.Clock.InstallAsync());

        // Garbled only now, so the save saw a camera sending pictures and live view is offered.
        await Expect(scenario.LiveToggle).ToBeVisibleAsync(new() { Timeout = 15_000 });
        scenario.Host.Sidecar.AddCamera(scenario.Source, FakeCamera.Jpeg with { Garbled = true });

        // Recorded as it happens rather than read at the end, because the engines part ways here:
        // Chromium reports the undecodable part as an error and falls back at once, WebKit keeps the
        // stream open and waits. Either is honest; claiming "live" for a moment in between is not.
        await scenario.Caption.EvaluateAsync("""
            (caption, live) => new MutationObserver(() => {
                if (caption.textContent === live) {
                    window.calledLive = true;
                }
            }).observe(caption, { childList: true, characterData: true, subtree: true })
            """, await scenario.LabelAsync("live"));

        await scenario.LiveToggle.ClickAsync();
        await scenario.Page.Clock.RunForAsync(1_000);
        await scenario.Page.Clock.FastForwardAsync("00:11");

        await Expect(scenario.LiveNote).ToHaveTextAsync(await scenario.LabelAsync("failed"), new() { Timeout = 10_000 });
        (await scenario.Page.EvaluateAsync<bool>("() => window.calledLive === true")).Should().BeFalse(
            "a stream nothing could be decoded from was never a picture");
        scenario.Host.Sidecar.Requests.Should().Contain(request => request.StartsWith("GET /api/stream.mjpeg", System.StringComparison.Ordinal),
                                                        "the stream must have been asked for, or not calling it live proves nothing");
    }

    /// <summary>
    /// A view that keeps playing stays live: watching the picture for loss must not take down a stream
    /// that is still arriving.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task AViewThatKeepsPlayingStaysLive(string engine)
    {
        await using CameraScenario scenario = await OpenJpegAsync(engine, "live-stays");
        await scenario.WatchLiveAsync();

        // Real time, past the two readings a lost stream needs.
        await Task.Delay(4_000, TestContext.Current.CancellationToken);

        await Expect(scenario.Caption).ToHaveTextAsync(await scenario.LabelAsync("live"));
        await Expect(scenario.LiveNote).ToBeEmptyAsync();
        scenario.Host.Sidecar.OpenMjpegStreams.Should().Be(1);
    }

    /// <summary>
    /// A stream the sidecar drops mid-view - its restart, or the camera going away - sends the panel
    /// back to the still and says the picture stopped, instead of leaving the last frame under "Live".
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task AViewCutAtTheSidecarFallsBackToTheStill(string engine)
    {
        await using CameraScenario scenario = await OpenJpegAsync(engine, "live-cut");
        await scenario.WatchLiveAsync();

        await scenario.Host.Sidecar.CutMjpegStreamsAsync();

        await Expect(scenario.LiveNote).ToHaveTextAsync(await scenario.LabelAsync("stalled"), new() { Timeout = 10_000 });
        await Expect(scenario.Caption).Not.ToHaveTextAsync(await scenario.LabelAsync("live"));
        await Expect(scenario.LiveToggle).ToHaveAttributeAsync("aria-label", await scenario.LabelAsync("watch"));
        await Expect(scenario.Image).ToHaveAttributeAsync("src", new System.Text.RegularExpressions.Regex("^blob:"),
                                                          new() { Timeout = 10_000 });
    }

    /// <summary>
    /// A view whose connection to Homespool breaks - a restart or a redeploy - says the picture
    /// stopped, instead of leaving "Live" over a broken picture.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task AViewCutByAHomespoolRestartSaysSo(string engine)
    {
        await using CameraScenario scenario = await OpenJpegAsync(engine, "live-restart");
        await scenario.WatchLiveAsync();

        await scenario.Host.Factory.DisposeAsync();

        await Expect(scenario.LiveNote).ToHaveTextAsync(await scenario.LabelAsync("stalled"), new() { Timeout = 10_000 });
        await Expect(scenario.Caption).Not.ToHaveTextAsync(await scenario.LabelAsync("live"));
        await Expect(scenario.LiveToggle).ToHaveAttributeAsync("aria-label", await scenario.LabelAsync("watch"));
    }

    /// <summary>
    /// Stopping live view closes the stream all the way back to the sidecar, and the still returns.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task StoppingLiveViewReleasesTheCamera(string engine)
    {
        await using CameraScenario scenario = await OpenJpegAsync(engine, "live-stops");
        await scenario.WatchLiveAsync();

        await scenario.LiveToggle.ClickAsync();

        await ExpectReleasedAsync(engine, scenario, "a stream the viewer has left must not hold the camera open for nobody");
        await Expect(scenario.LiveToggle).ToHaveAttributeAsync("aria-label", await scenario.LabelAsync("watch"));
        await Expect(scenario.Image).ToHaveAttributeAsync("src", new System.Text.RegularExpressions.Regex("^blob:"),
                                                          new() { Timeout = 10_000 });
        await Expect(scenario.LiveNote).ToBeEmptyAsync();
    }

    /// <summary>
    /// A tab that goes into the background stops watching, and the sidecar's stream closes with it.
    /// </summary>
    /// <remarks>
    /// A headless page is never hidden, so the page is told it is: <c>document.hidden</c> and the
    /// event the page listens for, which is the whole of what the script reads.
    /// </remarks>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task AHiddenTabStopsWatching(string engine)
    {
        await using CameraScenario scenario = await OpenJpegAsync(engine, "live-hidden");
        await scenario.WatchLiveAsync();

        await scenario.Page.EvaluateAsync("""
            () => {
                Object.defineProperty(document, "hidden", { configurable: true, get: () => true });
                document.dispatchEvent(new Event("visibilitychange"));
            }
            """);

        await Expect(scenario.LiveToggle).ToHaveAttributeAsync("aria-label", await scenario.LabelAsync("watch"));
        await ExpectReleasedAsync(engine, scenario, "a background tab must not hold a camera open for nobody");
    }

    /// <summary>
    /// A camera whose codecs nobody could learn is not offered live view at all: the page asks, and
    /// shows no button when the answer is no.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task ACameraThatCannotBeWatchedOffersNoButton(string engine)
    {
        await using CameraScenario scenario = await CameraScenario.OpenAsync(browsers, engine, "live-absent", FakeCamera.Absent);

        // Quiet between the still's polls, by which time the one question about live view has been
        // asked and answered.
        await scenario.Page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);

        await Expect(scenario.LiveToggle).ToBeHiddenAsync();
        (await scenario.Page.EvaluateAsync<bool>(
             "async () => (await (await fetch(document.querySelector('[data-camera-live]').dataset.cameraLive)).json()).available"))
            .Should().BeFalse("the button's absence has to be the page obeying a no, not the question never being answered");
    }

    /// <summary>
    /// A camera that produces nothing when live view starts sends the panel back to the still, with the
    /// reason under it.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task ACameraThatSendsNothingFallsBackToTheStill(string engine)
    {
        await using CameraScenario scenario = await OpenJpegAsync(engine, "live-dark");

        // Switched off after the page offered live view, so the button is there to press.
        await Expect(scenario.LiveToggle).ToBeVisibleAsync(new() { Timeout = 15_000 });
        scenario.Host.Sidecar.AddCamera(scenario.Source, FakeCamera.Jpeg with { Producing = false });

        await scenario.LiveToggle.ClickAsync();

        // Well inside the page's ten-second deadline: a refused stream is reported when it is refused,
        // not when the viewer has given up waiting.
        await Expect(scenario.LiveNote).ToHaveTextAsync(await scenario.LabelAsync("failed"), new() { Timeout = 5_000 });
        await Expect(scenario.LiveToggle).ToHaveAttributeAsync("aria-label", await scenario.LabelAsync("watch"));
        await Expect(scenario.Caption).Not.ToHaveTextAsync(await scenario.LabelAsync("live"));
    }

    /// <summary>
    /// An H.264 camera is offered over WebRTC: the browser's offer reaches the sidecar through
    /// Homespool, and an answer that cannot connect sends the panel back to the still, saying so.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task AWebRtcAnswerThatCannotConnectFallsBack(string engine)
    {
        await using CameraScenario scenario = await CameraScenario.OpenAsync(
            browsers, engine, "webrtc-unusable", FakeCamera.H264, configure: WithWebRtcAddress);

        await scenario.StartLiveAsync();

        // Inside the deadline, as above: an answer that cannot be used is known the moment it arrives.
        await Expect(scenario.LiveNote).ToHaveTextAsync(await scenario.LabelAsync("failed"), new() { Timeout = 5_000 });
        await Expect(scenario.LiveToggle).ToHaveAttributeAsync("aria-label", await scenario.LabelAsync("watch"));
        scenario.Host.Sidecar.Offers.Should().ContainSingle()
                .Which.Should().StartWith("v=0", "the offer is the browser's own session description, passed through");
    }

    /// <summary>
    /// A sidecar that never answers the offer does not leave the viewer waiting: the page's own
    /// deadline sends the panel back to the still.
    /// </summary>
    /// <remarks>
    /// The deadline is ten seconds, so the page's clock is run forward past it rather than waited out.
    /// </remarks>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task AnOfferNobodyAnswersFallsBackAtTheDeadline(string engine)
    {
        await using CameraScenario scenario = await CameraScenario.OpenAsync(
            browsers, engine, "webrtc-hang", FakeCamera.H264 with { Offer = FakeOfferAnswer.Hang },
            configure: WithWebRtcAddress,
            beforePage: context => context.Clock.InstallAsync());

        await scenario.StartLiveAsync();
        (await CameraScenario.EventuallyAsync(() => scenario.Host.Sidecar.Offers.Any())).Should().BeTrue(
            "the offer must be waiting on the sidecar, or the deadline has nothing to end");

        await scenario.Page.Clock.FastForwardAsync("00:11");

        await Expect(scenario.LiveNote).ToHaveTextAsync(await scenario.LabelAsync("failed"), new() { Timeout = 10_000 });
        await Expect(scenario.LiveToggle).ToHaveAttributeAsync("aria-label", await scenario.LabelAsync("watch"));
    }

    /// <summary>A Motion-JPEG camera, as Homespool's own sidecar image streams it.</summary>
    private Task<CameraScenario> OpenJpegAsync(string engine, string name)
    {
        return CameraScenario.OpenAsync(browsers, engine, name, FakeCamera.Jpeg);
    }

    /// <summary>
    /// The sidecar's stream ended with the viewer's - in the engines that end it; see the remarks on
    /// this class for the one that does not.
    /// </summary>
    private static async Task ExpectReleasedAsync(string engine, CameraScenario scenario, string because)
    {
        if (engine == Browsers.WebKit)
        {
            return;
        }

        (await CameraScenario.EventuallyAsync(() => scenario.Host.Sidecar.OpenMjpegStreams == 0)).Should().BeTrue(because);
    }

    /// <summary>An address for WebRTC media, without which no camera is offered over it.</summary>
    private static void WithWebRtcAddress(HomespoolFactory factory)
    {
        factory.ConfigurationOverrides["Cameras:WebRtcCandidate"] = "192.0.2.10:8555";
    }
}
