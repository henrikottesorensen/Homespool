using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

using Homespool.Data;
using Homespool.Host.E2ETest;
using Homespool.Host.PrintFiles;
using Homespool.Model;
using Homespool.Model.Entities;

using static Microsoft.Playwright.Assertions;

namespace Homespool.Host.BrowserTest;

/// <summary>
/// The pictures at the top of the printer page, in a real browser - <c>camera-deck.js</c>: stepping
/// through cameras, remembering the choice, parking the ones not on show, and the preview taking the
/// second place when a print starts.
/// </summary>
public sealed class CameraDeckTests(Browsers browsers)
{
    /// <summary>A real 1x1 PNG.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    /// <summary>
    /// Three cameras, nothing printing: two on show. Stepping the left one on skips the camera already
    /// on the right, keeps the keyboard on the switcher, and is remembered across a reload.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task SteppingSkipsTheCameraBesideItAndIsRemembered(string engine)
    {
        await using Deck deck = await Deck.OpenAsync(browsers, engine, "deck-step", cameras: 3);

        (await deck.OnShowAsync()).Should().Equal("Camera 1", "Camera 2");

        await deck.Switcher("Camera 1", step: 1).ClickAsync();

        (await deck.OnShowAsync()).Should().Equal("Camera 3", "Camera 2");
        await Expect(deck.Switcher("Camera 3", step: 1)).ToBeFocusedAsync();

        await deck.Page.ReloadAsync();

        (await deck.OnShowAsync()).Should().Equal("Camera 3", "Camera 2");
    }

    /// <summary>
    /// A camera stepped away from stops asking for pictures. The server captures only while somebody
    /// asks, so a hidden camera left polling would keep one busy for nobody.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task ACameraNotOnShowAsksForNothing(string engine)
    {
        await using Deck deck = await Deck.OpenAsync(browsers, engine, "deck-park", cameras: 3);

        string first = await deck.FrameUrlAsync("Camera 1");
        string third = await deck.FrameUrlAsync("Camera 3");

        ConcurrentQueue<string> asked = new();
        deck.Page.Request += (_, request) => asked.Enqueue(new Uri(request.Url).AbsolutePath);

        // Hidden from the start, camera 3 is never asked for - not even once, before anything parks it.
        await Task.Delay(3_000, TestContext.Current.CancellationToken);

        asked.Should().Contain(first, "camera 1 is on show");
        asked.Should().NotContain(third, "camera 3 has not been on show");

        // And asked for as soon as it is.
        await deck.Switcher("Camera 1", step: 1).ClickAsync();
        await deck.Page.WaitForRequestAsync(request => new Uri(request.Url).AbsolutePath == third, new() { Timeout = 15_000 });

        // An interval and a half of the poll's, from a clean count.
        asked.Clear();
        await Task.Delay(3_000, TestContext.Current.CancellationToken);

        asked.Should().NotContain(first, "camera 1 is no longer on show");
        asked.Should().Contain(third, "camera 3 is, and keeps being asked for");
    }

    /// <summary>
    /// A print starting while the page is open gives the second place to its preview, and the second
    /// camera goes behind the switcher; the print ending brings it back. Both arrive on the status
    /// card's poll, with nothing reloaded.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task APrintTakesTheSecondPlaceWhileItRuns(string engine)
    {
        await using Deck deck = await Deck.OpenAsync(browsers, engine, "deck-print", cameras: 2);

        (await deck.OnShowAsync()).Should().Equal("Camera 1", "Camera 2");
        await Expect(deck.Preview).ToBeHiddenAsync();

        Guid print = await deck.StartPrintAsync();

        await Expect(deck.Preview).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(deck.Preview.Locator("img")).ToHaveJSPropertyAsync("naturalWidth", 1);
        (await deck.OnShowAsync()).Should().Equal("Camera 1");
        await Expect(deck.Switcher("Camera 1", step: 1)).ToBeVisibleAsync();

        await deck.EndPrintAsync(print);

        await Expect(deck.Preview).ToBeHiddenAsync(new() { Timeout = 15_000 });
        (await deck.OnShowAsync()).Should().Equal("Camera 1", "Camera 2");
        await Expect(deck.Switcher("Camera 1", step: 1)).ToBeHiddenAsync();
    }

    /// <summary>
    /// A printer with no camera: nothing at the top until a print starts, then the preview alone, and
    /// nothing again when it ends.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task APrinterWithNoCameraShowsThePreviewWhilePrinting(string engine)
    {
        await using Deck deck = await Deck.OpenAsync(browsers, engine, "deck-no-camera", cameras: 0);

        ILocator row = deck.Page.Locator("[data-camera-deck]");
        await Expect(row).ToBeHiddenAsync();

        Guid print = await deck.StartPrintAsync();

        await Expect(deck.Preview).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(deck.Preview.Locator("img")).ToHaveJSPropertyAsync("naturalWidth", 1);

        await deck.EndPrintAsync(print);

        await Expect(row).ToBeHiddenAsync(new() { Timeout = 15_000 });
    }

    /// <summary>A printer page with some cameras on it, open in a browser.</summary>
    private sealed class Deck : IAsyncDisposable
    {
        private readonly CameraHost _host;
        private readonly IBrowserContext _context;
        private readonly Guid _printer;
        private readonly string _email;

        private Deck(CameraHost host, IBrowserContext context, IPage page, Guid printer, string email)
        {
            _host = host;
            _context = context;
            Page = page;
            _printer = printer;
            _email = email;
        }

        public IPage Page { get; }

        public ILocator Preview => Page.Locator("[data-deck-thumbnail]");

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                         Justification = "The host is owned by the deck returned, which disposes it; the catch disposes it when there is none.")]
        public static async Task<Deck> OpenAsync(Browsers browsers, string engine, string name, int cameras)
        {
            CameraHost host = await CameraHost.StartAsync($"browser-{name}");

            try
            {
                string email = $"{name}@example.com";
                (HttpClient client, Guid printer) = await host.AccountWithPrinterAsync(email);

                using (client)
                {
                    HSUser user = await UserAsync(host, email);

                    for (int index = 1; index <= cameras; index++)
                    {
                        string source = $"rtsp://192.0.2.1/{name}-{index}";
                        host.Sidecar.AddCamera(source, FakeCamera.Jpeg);
                        await CameraPage.AddNetworkCameraAsync(host.Factory, client, user, $"Camera {index}", source, printer);
                    }
                }

                IBrowserContext context = await browsers.NewContextAsync(engine, host.BaseAddress, locale: null);
                IPage page = await CameraHost.OpenPrinterPageAsync(context, email, printer);

                return new Deck(host, context, page, printer, email);
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        /// <summary>The captions of the cameras on show, left to right.</summary>
        public async Task<string[]> OnShowAsync()
        {
            return await Page.EvaluateAsync<string[]>("""
                () => [...document.querySelectorAll("[data-deck-camera]")]
                    .filter(item => !item.hidden)
                    .sort((a, b) => a.getBoundingClientRect().left - b.getBoundingClientRect().left)
                    .map(item => item.querySelector("figcaption span").textContent.trim())
                """);
        }

        /// <summary>The previous (-1) or next (1) button above the camera captioned <paramref name="camera"/>.</summary>
        public ILocator Switcher(string camera, int step)
        {
            return Page.Locator("[data-deck-camera]")
                       .Filter(new() { Has = Page.Locator("figcaption span", new() { HasTextRegex = new Regex($"^{Regex.Escape(camera)}$") }) })
                       .Locator($"[data-deck-step=\"{step}\"]");
        }

        /// <summary>The path a camera's still is polled at.</summary>
        public async Task<string> FrameUrlAsync(string camera)
        {
            string? url = await Page.Locator("[data-deck-camera]")
                                    .Filter(new() { HasText = camera })
                                    .Locator("[data-camera-frame]")
                                    .GetAttributeAsync("data-camera-frame");

            return url ?? throw new InvalidOperationException($"{camera} has no frame address.");
        }

        /// <summary>Stores a file with a preview and opens a print of it, as the queue would.</summary>
        public async Task<Guid> StartPrintAsync()
        {
            using IServiceScope scope = _host.Factory.Services.CreateScope();
            HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();
            PrintFileCatalog catalog = scope.ServiceProvider.GetRequiredService<PrintFileCatalog>();
            HSUser user = await UserAsync(_host, _email);

            string encoded = Convert.ToBase64String(Png);
            string gcode = $"; thumbnail begin 380x285 {encoded.Length}\n; {encoded}\n; thumbnail end\n\nG28\n";

            await catalog.SaveAsync(Caller.Unscoped(user.Id), "cube.gcode", new MemoryStream(Encoding.ASCII.GetBytes(gcode)),
                                    overwrite: false, TestContext.Current.CancellationToken);

            int printerId = await context.Printers.Where(printer => printer.Uuid == _printer)
                                         .Select(printer => printer.Id)
                                         .SingleAsync(TestContext.Current.CancellationToken);

            Guid print = Guid.NewGuid();

            context.PrintJobs.Add(new PrintJob
            {
                PrintUuid = print,
                PrinterId = printerId,
                FileName = "cube.gcode",
                QueuedByUserId = user.Id,
                State = PrintState.Printing,
                StartedAt = DateTimeOffset.UtcNow,
            });

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            return print;
        }

        public async Task EndPrintAsync(Guid print)
        {
            using IServiceScope scope = _host.Factory.Services.CreateScope();
            HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

            await context.PrintJobs.Where(job => job.PrintUuid == print)
                         .ExecuteUpdateAsync(set => set.SetProperty(job => job.EndedAt, DateTimeOffset.UtcNow)
                                                       .SetProperty(job => job.State, PrintState.Finished),
                                             TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _context.DisposeAsync();
            await _host.DisposeAsync();
        }

        private static async Task<HSUser> UserAsync(CameraHost host, string email)
        {
            using IServiceScope scope = host.Factory.Services.CreateScope();
            UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

            return await users.FindByEmailAsync(email) ?? throw new InvalidOperationException($"No account for {email}.");
        }
    }
}
