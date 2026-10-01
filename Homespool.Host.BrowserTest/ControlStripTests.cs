using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

using Homespool.Data;
using Homespool.Host.Printing;
using Homespool.Model;
using Homespool.Model.Entities;

using static Microsoft.Playwright.Assertions;

namespace Homespool.Host.BrowserTest;

/// <summary>
/// The control strip on the printer page, in a real browser: brought up to date from the status card's
/// poll when the printer changes, without losing a filament somebody has chosen, and never while they
/// are in it.
/// </summary>
public sealed class ControlStripTests(Browsers browsers)
{
    /// <summary>Two and a half of the status card's polls - long enough that a swap would have happened.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A page opened while the printer was away gains Stop and Pause when it comes back printing, and
    /// trades Pause for Resume when it pauses - with nothing reloaded.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task TheStripFollowsThePrinterWithoutAReload(string engine)
    {
        await using Strip strip = await Strip.OpenAsync(browsers, engine, "strip-follow");

        await Expect(strip.Handler("Preheat")).ToBeVisibleAsync();
        await Expect(strip.Handler("Stop")).ToHaveCountAsync(0);

        await strip.ReportAsync(PrinterStatus.Printing);
        strip.Connect();

        await Expect(strip.Handler("Stop")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(strip.Handler("Pause")).ToBeVisibleAsync();
        await Expect(strip.Handler("Resume")).ToHaveCountAsync(0);

        await strip.ReportAsync(PrinterStatus.Paused);

        await Expect(strip.Handler("Resume")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(strip.Handler("Pause")).ToHaveCountAsync(0);
        await Expect(strip.Handler("Stop")).ToBeVisibleAsync();
    }

    /// <summary>
    /// A filament chosen and not yet sent is still chosen after the strip is redrawn around it.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task AChosenFilamentSurvivesTheStripBeingRedrawn(string engine)
    {
        await using Strip strip = await Strip.OpenAsync(browsers, engine, "strip-choice");

        await strip.Filament.SelectOptionAsync("PETG");
        await strip.LeaveAsync();

        await strip.ReportAsync(PrinterStatus.Printing);
        strip.Connect();

        await Expect(strip.Handler("Pause")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(strip.Filament).ToHaveValueAsync("PETG");
    }

    /// <summary>
    /// Nothing is redrawn under somebody using the strip: the change waits, and lands as they leave it.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task TheStripWaitsForItsReaderToLeave(string engine)
    {
        await using Strip strip = await Strip.OpenAsync(browsers, engine, "strip-wait");

        await strip.Filament.FocusAsync();

        await strip.ReportAsync(PrinterStatus.Printing);
        strip.Connect();

        // The card says so first - that is the poll that carries the strip.
        await Expect(strip.Page.Locator(".printer-status-badge")).ToHaveTextAsync("Printing", new() { Timeout = 15_000 });
        await Task.Delay(Settle, TestContext.Current.CancellationToken);

        await Expect(strip.Handler("Stop")).ToHaveCountAsync(0);
        await Expect(strip.Filament).ToBeFocusedAsync();

        await strip.LeaveAsync();

        await Expect(strip.Handler("Stop")).ToBeVisibleAsync(new() { Timeout = 5_000 });
    }

    /// <summary>
    /// The lighting slider shows its number as it moves, before anything is sent.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task TheSliderShowsItsNumberAsItMoves(string engine)
    {
        await using Strip strip = await Strip.OpenAsync(browsers, engine, "strip-slider");

        await strip.ReportAsync(PrinterStatus.Idle, lighting: 60);
        strip.Connect();

        await Expect(strip.Slider).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(strip.SliderNumber).ToHaveTextAsync("60");

        await strip.Slider.FocusAsync();
        await strip.Page.Keyboard.PressAsync("ArrowLeft");
        await strip.Page.Keyboard.PressAsync("ArrowLeft");

        await Expect(strip.Slider).ToHaveValueAsync("58");
        await Expect(strip.SliderNumber).ToHaveTextAsync("58");
    }

    /// <summary>
    /// A slider moved and not yet sent keeps its place, and its number, when the strip is redrawn
    /// around it for something else.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task AMovedSliderSurvivesTheStripBeingRedrawn(string engine)
    {
        await using Strip strip = await Strip.OpenAsync(browsers, engine, "strip-moved");

        await strip.ReportAsync(PrinterStatus.Idle, lighting: 60);
        strip.Connect();

        await Expect(strip.Slider).ToBeVisibleAsync(new() { Timeout = 15_000 });

        await strip.Slider.FocusAsync();
        await strip.Page.Keyboard.PressAsync("ArrowLeft");
        await strip.LeaveAsync();

        await strip.ReportAsync(PrinterStatus.Printing, lighting: 60);

        await Expect(strip.Handler("Pause")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(strip.Slider).ToHaveValueAsync("59");
        await Expect(strip.SliderNumber).ToHaveTextAsync("59");
    }

    /// <summary>
    /// A slider nobody has touched follows the printer's report - a change made at the panel, or the
    /// brightness a press of Set left behind.
    /// </summary>
    [Theory]
    [InlineData(Browsers.Chromium)]
    [InlineData(Browsers.WebKit)]
    public async Task AnUntouchedSliderFollowsTheReport(string engine)
    {
        await using Strip strip = await Strip.OpenAsync(browsers, engine, "strip-report");

        await strip.ReportAsync(PrinterStatus.Idle, lighting: 60);
        strip.Connect();

        await Expect(strip.Slider).ToHaveValueAsync("60", new() { Timeout = 15_000 });

        await strip.ReportAsync(PrinterStatus.Idle, lighting: 20);

        await Expect(strip.Slider).ToHaveValueAsync("20", new() { Timeout = 15_000 });
        await Expect(strip.SliderNumber).ToHaveTextAsync("20");
    }

    /// <summary>A printer page open in a browser, for a printer that is not connected.</summary>
    private sealed class Strip : IAsyncDisposable
    {
        private readonly CameraHost _host;
        private readonly IBrowserContext _context;
        private readonly int _printerId;

        private Strip(CameraHost host, IBrowserContext context, IPage page, int printerId)
        {
            _host = host;
            _context = context;
            Page = page;
            _printerId = printerId;
        }

        public IPage Page { get; }

        public ILocator Filament => Page.Locator("[data-live-target=\"printer-controls\"] #filament");

        public ILocator Slider => Page.Locator("[data-live-target=\"printer-controls\"] #lighting");

        public ILocator SliderNumber => Page.Locator("[data-live-target=\"printer-controls\"] #lighting-value");

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                         Justification = "The host is owned by the strip returned, which disposes it; the catch disposes it when there is none.")]
        public static async Task<Strip> OpenAsync(Browsers browsers, string engine, string name)
        {
            CameraHost host = await CameraHost.StartAsync($"browser-{name}");

            try
            {
                string email = $"{name}@example.com";
                (HttpClient client, Guid printer) = await host.AccountWithPrinterAsync(email);

                client.Dispose();

                int printerId = await PrinterIdAsync(host, printer);

                IBrowserContext context = await browsers.NewContextAsync(engine, host.BaseAddress, locale: null);
                IPage page = await CameraHost.OpenPrinterPageAsync(context, email, printer);

                return new Strip(host, context, page, printerId);
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        /// <summary>The form posting <paramref name="handler"/>, in the strip on the page.</summary>
        public ILocator Handler(string handler)
        {
            return Page.Locator($"[data-live-target=\"printer-controls\"] form[action*=\"handler={handler}\"]");
        }

        /// <summary>Puts the printer in the connection registry, which is all the page asks of a connection.</summary>
        public void Connect()
        {
            _host.Factory.Services.GetRequiredService<PrinterConnectionRegistry>()
                 .Register(_printerId, new OpenLink(), overPlaintext: false);
        }

        /// <summary>
        /// Writes the printer's live state as telemetry would: <paramref name="status"/>, with PLA
        /// loaded, and the light at <paramref name="lighting"/> when there is one.
        /// </summary>
        public async Task ReportAsync(PrinterStatus status, int? lighting = null)
        {
            using IServiceScope scope = _host.Factory.Services.CreateScope();
            HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

            PrinterLiveState? live = await context.PrinterLiveStates
                                                  .SingleOrDefaultAsync(state => state.PrinterId == _printerId,
                                                                        TestContext.Current.CancellationToken);

            if (live is null)
            {
                live = new PrinterLiveState { PrinterId = _printerId };
                context.PrinterLiveStates.Add(live);
            }

            live.Status = status;
            live.Material = "PLA";
            live.ChamberLedIntensity = lighting;
            live.LastSeenAt = DateTimeOffset.UtcNow;

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>Takes focus out of the strip, as somebody moving on to the rest of the page would.</summary>
        public async Task LeaveAsync()
        {
            await Page.EvaluateAsync("() => document.activeElement && document.activeElement.blur()");
        }

        public async ValueTask DisposeAsync()
        {
            await _context.DisposeAsync();
            await _host.DisposeAsync();
        }

        private static async Task<int> PrinterIdAsync(CameraHost host, Guid printer)
        {
            using IServiceScope scope = host.Factory.Services.CreateScope();
            HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

            return await context.Printers.Where(row => row.Uuid == printer)
                                .Select(row => row.Id)
                                .SingleAsync(TestContext.Current.CancellationToken);
        }
    }

    private sealed class OpenLink : IPrinterLink
    {
        public bool IsOpen => true;

        public Task<CommandSendResult> SendAsync(IPrinterIntent intent, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("these tests render a page and send the printer nothing");
        }

        public void Complete()
        {
        }
    }
}
