using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Homespool.Host.BrowserTest;

/// <summary>
/// The Files page's table at a phone's width and at a desktop's - <c>.file-table</c> in
/// <c>site.css</c>, which only a browser lays out.
/// </summary>
/// <remarks>
/// <para>
/// <b>At a phone's width each file is a block</b> - its name across the top, size and date under it,
/// and its buttons wrapping below. As a table, one row of buttons that would not wrap made the page
/// wider than the screen: Download and Delete went off its right edge, and the name, the one column
/// left to give way, was squeezed to four characters a line.
/// </para>
/// <para>
/// <b>A printer is chosen</b>, because only then does a row carry Send and Queue, which is the
/// widest a row gets. The name is a real slicer's, which is the length that broke it.
/// </para>
/// </remarks>
public sealed class FilesPageLayoutTests(Browsers browsers)
{
    private const string LongName = "sheet_holder_rod_v2_0.6n_0.25mm_PLA_COREONE_16m.gcode";

    /// <summary>An iPhone 15 Pro's viewport, in CSS pixels.</summary>
    private const int PhoneWidth = 393;

    private const int PhoneHeight = 852;

    /// <summary>
    /// On a phone the page is no wider than the screen, every button of every file is wholly on it,
    /// and the name has most of the width rather than what the buttons leave.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task OnAPhoneEveryButtonIsOnScreenAndThePageDoesNotScrollSideways(string engine)
    {
        await using CameraHost host = await CameraHost.StartAsync($"browser-files-phone-{engine}");
        await using IBrowserContext context = await OpenAsync(host, engine, "files-phone", PhoneWidth, PhoneHeight);
        IPage page = context.Pages[0];

        (await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth"))
            .Should().BeLessThanOrEqualTo(PhoneWidth, "a page wider than the screen scrolls sideways, which is where the buttons went");

        ILocator buttons = page.Locator("table tbody .btn");
        await Expect(buttons).ToHaveCountAsync(10);

        foreach (ILocator button in await buttons.AllAsync())
        {
            LocatorBoundingBoxResult box = await button.BoundingBoxAsync() ??
                                           throw new InvalidOperationException("A file's button is not rendered at all.");

            box.X.Should().BeGreaterThanOrEqualTo(0, $"{await button.TextContentAsync()} has to start on the screen");
            (box.X + box.Width).Should().BeLessThanOrEqualTo(PhoneWidth, $"{await button.TextContentAsync()} has to end on the screen");
        }

        LocatorBoundingBoxResult name = await page.Locator("table tbody td", new() { HasText = LongName }).BoundingBoxAsync() ??
                                        throw new InvalidOperationException("The long name is not rendered.");

        name.Width.Should().BeGreaterThan(PhoneWidth / 2, "the name is what a person reads the row by, so it gets the width");
    }

    /// <summary>
    /// At a desktop's width the phone layout stays out of the way: rows are table rows, and each
    /// file's buttons sit on one line.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task OnADesktopTheFilesAreStillATable(string engine)
    {
        await using CameraHost host = await CameraHost.StartAsync($"browser-files-desktop-{engine}");
        await using IBrowserContext context = await OpenAsync(host, engine, "files-desktop", 1280, 800);
        IPage page = context.Pages[0];

        ILocator row = page.Locator("table tbody tr", new() { HasText = LongName });

        (await row.EvaluateAsync<string>("row => getComputedStyle(row).display")).Should().Be("table-row");

        float? top = null;

        foreach (ILocator button in await row.Locator(".btn").AllAsync())
        {
            LocatorBoundingBoxResult box = await button.BoundingBoxAsync() ??
                                           throw new InvalidOperationException("A file's button is not rendered at all.");

            top ??= box.Y;
            box.Y.Should().BeApproximately(top.Value, 1f, $"{await button.TextContentAsync()} belongs on the same line as the others");
        }

        top.Should().NotBeNull("the row has buttons to compare");
    }

    /// <summary>
    /// Signs in as an account with a printer and two files, one of them with a long name, and opens
    /// the Files page at the given size with that printer chosen - the context's one page.
    /// </summary>
    private async Task<IBrowserContext> OpenAsync(CameraHost host, string engine, string name, int width, int height)
    {
        string email = $"{name}@example.com";
        (HttpClient client, Guid printer) = await host.AccountWithPrinterAsync(email);

        using (client)
        {
            await UploadAsync(client, LongName);
            await UploadAsync(client, "benchy.gcode");
        }

        IBrowserContext context = await browsers.NewContextAsync(engine, host.BaseAddress);

        try
        {
            IPage page = await CameraHost.SignInAsync(context, email);

            await page.SetViewportSizeAsync(width, height);
            await page.GotoAsync($"/Files?printerUuid={printer}");

            return context;
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }

    private static async Task UploadAsync(HttpClient client, string name)
    {
        using StreamContent body = new(new MemoryStream(Encoding.UTF8.GetBytes(new string('G', 2048))));
        using HttpResponseMessage response = await client.PutAsync(
            new Uri($"/api/v1/files/{Uri.EscapeDataString(name)}", UriKind.Relative), body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture upload has to have worked");
    }
}
