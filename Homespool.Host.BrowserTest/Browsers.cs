using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Playwright;

[assembly: AssemblyFixture(typeof(Homespool.Host.BrowserTest.Browsers))]

namespace Homespool.Host.BrowserTest;

/// <summary>
/// One Playwright driver and one browser per engine for the whole run, each test getting a context of
/// its own - a fresh profile, cookies and all - rather than a browser of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Chromium, and WebKit as the stand-in for Safari.</b> It is Playwright's build of WebKit, not
/// Safari, so what it shows about Safari is evidence rather than proof; it is still the only engine
/// here that shares Safari's image decoder and its multipart handling, which is where this page's
/// worst failures have lived.
/// </para>
/// <para>
/// <b>A browser that is not installed skips the test, it does not fail it</b>, the way the dex and
/// sidecar fixtures do: the browsers are a download of their own, fetched by
/// <c>install-browsers.sh</c>, and a machine without them has nothing to test with.
/// </para>
/// </remarks>
public sealed class Browsers : IAsyncLifetime
{
    public const string Chromium = "chromium";
    public const string WebKit = "webkit";

    private readonly Dictionary<string, IBrowser> _launched = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _launching = new(1, 1);

    private IPlaywright? _playwright;

    public async ValueTask InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IBrowser browser in _launched.Values)
        {
            await browser.CloseAsync();
        }

        _playwright?.Dispose();
        _launching.Dispose();
    }

    /// <summary>
    /// A fresh context in the named engine, pointed at <paramref name="baseAddress"/>. Skips the
    /// calling test when that engine is not installed. <paramref name="locale"/> is the language the
    /// browser asks for - <c>da-DK</c>, say - or null for Playwright's default; it reaches the server
    /// as <c>Accept-Language</c>, which is how an account with no language of its own is served.
    /// </summary>
    public async Task<IBrowserContext> NewContextAsync(string engine, Uri baseAddress, string? locale = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        IBrowser browser;

        try
        {
            browser = await BrowserAsync(engine);
        }
        catch (PlaywrightException exception) when (exception.Message.Contains("Executable doesn't exist", StringComparison.Ordinal))
        {
            Assert.Skip($"{engine} is not installed for Playwright. Run Homespool.Host.BrowserTest/install-browsers.sh.");
            throw;
        }

        return await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = baseAddress.ToString(), Locale = locale });
    }

    /// <summary>The engine's one browser, launched by whichever test asks for it first.</summary>
    private async Task<IBrowser> BrowserAsync(string engine)
    {
        await _launching.WaitAsync(TestContext.Current.CancellationToken);

        try
        {
            if (!_launched.TryGetValue(engine, out IBrowser? browser))
            {
                browser = await LaunchAsync(engine);
                _launched[engine] = browser;
            }

            return browser;
        }
        finally
        {
            _launching.Release();
        }
    }

    private Task<IBrowser> LaunchAsync(string engine)
    {
        IPlaywright playwright = _playwright ?? throw new InvalidOperationException("The driver is started first.");

        IBrowserType type = engine switch
        {
            Chromium => playwright.Chromium,
            WebKit => playwright.Webkit,
            _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Not an engine these tests run."),
        };

        return type.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }
}
