using System.Threading.Tasks;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Homespool.Host.BrowserTest;

/// <summary>
/// The capture sizes offered beside the attached-camera picker, in a real browser -
/// <c>camera-resolution.js</c>.
/// </summary>
/// <remarks>
/// <b>On a page of its own rather than the Cameras page.</b> That page lists attached cameras from the
/// host's own device directory, which a test host does not have, so the picker is never rendered
/// there. The markup here is the picker's shape - a device select naming its target, each device
/// carrying its sizes - served from the application's origin so the script loads as it does for real.
/// </remarks>
public sealed class CameraResolutionTests(Browsers browsers)
{
    private const string Picker = """
        <select id="devices" data-resolution-target="sizes">
            <option value="usb-a" data-sizes="640x480 1280x720 1920x1080">Camera A</option>
            <option value="usb-b" data-sizes="">Camera B</option>
            <option value="usb-c" data-sizes="320x240">Camera C</option>
        </select>
        <select id="sizes"><option value="">Whatever the camera provides</option></select>
        <script src="/js/camera-resolution.js"></script>
        """;

    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task ChoosingADeviceOffersItsSizesAndNothingElse(string engine)
    {
        await using CameraHost host = await CameraHost.StartAsync("browser-resolution");
        await using IBrowserContext context = await browsers.NewContextAsync(engine, host.BaseAddress);
        IPage page = await context.NewPageAsync();

        // Any page of the application's, for its origin; its content is then replaced.
        await page.GotoAsync("/Account/Login");
        await page.SetContentAsync(Picker);

        ILocator sizes = page.Locator("#sizes option");

        await Expect(sizes).ToHaveTextAsync(["Whatever the camera provides", "640x480", "1280x720", "1920x1080"]);

        await page.SelectOptionAsync("#devices", "usb-b");
        await Expect(sizes).ToHaveTextAsync(["Whatever the camera provides"]);

        await page.SelectOptionAsync("#devices", "usb-c");
        await Expect(sizes).ToHaveTextAsync(["Whatever the camera provides", "320x240"]);
        await Expect(page.Locator("#sizes")).ToHaveValueAsync(string.Empty);
    }
}
