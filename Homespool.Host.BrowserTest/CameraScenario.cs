using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

using Microsoft.Playwright;

using Homespool.Host.E2ETest;

using static Microsoft.Playwright.Assertions;

namespace Homespool.Host.BrowserTest;

/// <summary>
/// A printer page with one camera on it, open in a browser: the host, the fake sidecar, the camera
/// and the page, set up in the order a person would.
/// </summary>
public sealed class CameraScenario : IAsyncDisposable
{
    private CameraScenario(CameraHost host, string source, IBrowserContext context, IPage page)
    {
        Host = host;
        Source = source;
        Context = context;
        Page = page;
    }

    public CameraHost Host { get; }

    /// <summary>The camera's source, which is what the fake knows it by.</summary>
    public string Source { get; }

    public IBrowserContext Context { get; }

    public IPage Page { get; }

    /// <summary>The picture, still or live.</summary>
    public ILocator Image => Page.Locator(".camera-image");

    /// <summary>The live view's start and stop button.</summary>
    public ILocator LiveToggle => Page.Locator(".camera-live-toggle");

    /// <summary>The caption beside the picture: its age, or that it is live.</summary>
    public ILocator Caption => Page.Locator(".camera-age");

    /// <summary>The line under the picture that says why a live view ended.</summary>
    public ILocator LiveNote => Page.Locator(".camera-live-note");

    /// <summary>
    /// Starts a host, declares <paramref name="camera"/> to its sidecar, adds it through the Cameras
    /// page on a printer, and opens that printer's page signed in. <paramref name="beforePage"/> runs
    /// on the browser context before the page opens, for anything that must be in place from the
    /// page's first script - a clock, for one.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification = "The host is owned by the scenario returned, which disposes it; the catch disposes it when there is none.")]
    public static async Task<CameraScenario> OpenAsync(Browsers browsers,
                                                       string engine,
                                                       string name,
                                                       FakeCamera camera,
                                                       Action<HomespoolFactory>? configure = null,
                                                       Func<IBrowserContext, Task>? beforePage = null)
    {
        ArgumentNullException.ThrowIfNull(browsers);

        string source = $"rtsp://192.0.2.1/{name}";
        CameraHost host = await CameraHost.StartAsync($"browser-{name}", configure);

        try
        {
            host.Sidecar.AddCamera(source, camera);
            (string email, Guid printer) = await host.AccountWithCameraAsync($"{name}@example.com", source);

            IBrowserContext context = await browsers.NewContextAsync(engine, host.BaseAddress);

            if (beforePage is not null)
            {
                await beforePage(context);
            }

            IPage page = await CameraHost.OpenPrinterPageAsync(context, email, printer);

            return new CameraScenario(host, source, context, page);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    /// <summary>The label the page gives the live button for <paramref name="key"/>, in whatever language it rendered.</summary>
    public async Task<string> LabelAsync(string key)
    {
        return await LiveToggle.GetAttributeAsync($"data-label-{key}") ??
               throw new InvalidOperationException($"The live button carries no {key} label.");
    }

    /// <summary>Starts live view, once the page has offered it.</summary>
    public async Task StartLiveAsync()
    {
        await Expect(LiveToggle).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await LiveToggle.ClickAsync();
    }

    /// <summary>Starts a live MJPEG view and waits until the page says it is live.</summary>
    public async Task WatchLiveAsync()
    {
        await StartLiveAsync();
        await Expect(Caption).ToHaveTextAsync(await LabelAsync("live"), new() { Timeout = 15_000 });
    }

    /// <summary>Waits, a little at a time, for something only the sidecar can see.</summary>
    public static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return condition();
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await Host.DisposeAsync();
    }
}
