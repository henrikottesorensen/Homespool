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
/// The printer page's queue at a phone's width and at a desktop's - <c>.queue-table</c> in
/// <c>site.css</c>, which only a browser lays out.
/// </summary>
/// <remarks>
/// <para>
/// <b>At a phone's width each entry is a block</b> - its name on a line of its own, the smaller values
/// wrapping under it, the buttons below. As a table of seven columns, the one that held the buttons
/// would not wrap, and the file name was left two or three characters a line.
/// </para>
/// <para>
/// <b>Queued through the Files page's own buttons</b>, as a person would, so the entries are whatever
/// that path writes. Three of them, so the middle one has both move buttons live.
/// </para>
/// </remarks>
public sealed class PrintQueueLayoutTests(Browsers browsers)
{
    private const string LongName = "sheet_holder_rod_v2_0.6n_0.25mm_PLA_COREONE_16m.gcode";

    /// <summary>An iPhone 15 Pro's viewport, in CSS pixels.</summary>
    private const int PhoneWidth = 393;

    private const int PhoneHeight = 852;

    private static readonly string[] Names = [LongName, "E_0.6n_0.25mm_PLA_COREONE_8m.gcode", "benchy.gcode"];

    /// <summary>
    /// On a phone the queue is no wider than the screen, every button of every entry is wholly on
    /// it, and the name has most of the width rather than what the other columns leave.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task OnAPhoneEveryButtonIsOnScreenAndTheNameHasTheWidth(string engine)
    {
        await using CameraHost host = await CameraHost.StartAsync($"browser-queue-phone-{engine}");
        await using IBrowserContext context = await OpenAsync(host, engine, "queue-phone", PhoneWidth, PhoneHeight);
        IPage page = context.Pages[0];

        LocatorBoundingBoxResult table = await page.Locator(".queue-table").BoundingBoxAsync() ??
                                         throw new InvalidOperationException("The queue is not rendered.");

        (table.X + table.Width).Should().BeLessThanOrEqualTo(PhoneWidth, "a queue wider than the screen scrolls the page sideways");

        ILocator buttons = page.Locator(".queue-table tbody .btn");
        await Expect(buttons).ToHaveCountAsync(Names.Length * 3);

        foreach (ILocator button in await buttons.AllAsync())
        {
            LocatorBoundingBoxResult box = await button.BoundingBoxAsync() ??
                                           throw new InvalidOperationException("An entry's button is not rendered at all.");

            string label = await button.GetAttributeAsync("aria-label") ?? string.Empty;

            box.X.Should().BeGreaterThanOrEqualTo(0, $"{label} has to start on the screen");
            (box.X + box.Width).Should().BeLessThanOrEqualTo(PhoneWidth, $"{label} has to end on the screen");
        }

        LocatorBoundingBoxResult name = await page.Locator(".queue-table tbody td", new() { HasText = LongName }).BoundingBoxAsync() ??
                                        throw new InvalidOperationException("The long name is not rendered.");

        name.Width.Should().BeGreaterThan(PhoneWidth / 2, "the name is what a person reads the entry by, so it gets the width");
    }

    /// <summary>
    /// At a desktop's width the phone layout stays out of the way: entries are table rows, with
    /// their headings shown, and each entry's buttons sit on one line.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task OnADesktopTheQueueIsStillATable(string engine)
    {
        await using CameraHost host = await CameraHost.StartAsync($"browser-queue-desktop-{engine}");
        await using IBrowserContext context = await OpenAsync(host, engine, "queue-desktop", 1280, 800);
        IPage page = context.Pages[0];

        await Expect(page.Locator(".queue-table thead")).ToBeVisibleAsync();

        ILocator row = page.Locator(".queue-table tbody tr", new() { HasText = LongName });

        (await row.EvaluateAsync<string>("row => getComputedStyle(row).display")).Should().Be("table-row");

        float? top = null;

        foreach (ILocator button in await row.Locator(".btn").AllAsync())
        {
            LocatorBoundingBoxResult box = await button.BoundingBoxAsync() ??
                                           throw new InvalidOperationException("An entry's button is not rendered at all.");

            top ??= box.Y;
            box.Y.Should().BeApproximately(top.Value, 1f, $"{await button.GetAttributeAsync("aria-label")} belongs on the same line as the others");
        }

        top.Should().NotBeNull("the row has buttons to compare");
    }

    /// <summary>
    /// Signs in as an account with a printer, queues three files on it from the Files page, and
    /// opens the printer's page at the given size - the context's one page.
    /// </summary>
    private async Task<IBrowserContext> OpenAsync(CameraHost host, string engine, string name, int width, int height)
    {
        string email = $"{name}@example.com";
        (HttpClient client, Guid printer) = await host.AccountWithPrinterAsync(email);

        using (client)
        {
            foreach (string file in Names)
            {
                await UploadAsync(client, file);
            }
        }

        IBrowserContext context = await browsers.NewContextAsync(engine, host.BaseAddress);

        try
        {
            IPage page = await CameraHost.SignInAsync(context, email);

            await page.GotoAsync($"/Files?printerUuid={printer}");

            // In the order they should queue, each waiting for the page the post lands on before the
            // next click, so no click can meet a page that is on its way out.
            foreach (string file in Names)
            {
                ILocator queue = page.Locator("table tbody tr", new() { HasText = file })
                                     .Locator("button[formaction*='handler=Queue']");

                await page.RunAndWaitForResponseAsync(
                    () => queue.ClickAsync(),
                    response => response.Request.IsNavigationRequest && response.Request.Method == "GET");
                await page.WaitForLoadStateAsync(LoadState.Load);
            }

            await page.SetViewportSizeAsync(width, height);
            await page.GotoAsync($"/Printers/Detail/{printer}");
            await Expect(page.Locator(".queue-table tbody tr")).ToHaveCountAsync(Names.Length);

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
