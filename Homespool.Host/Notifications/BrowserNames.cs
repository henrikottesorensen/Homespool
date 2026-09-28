using System;

namespace Homespool.Host.Notifications;

/// <summary>
/// A name for the browser a subscription came from - "Firefox · macOS" - so that a list of them can be
/// told apart without anyone typing a name.
/// </summary>
/// <remarks>
/// <para>
/// <b>A label, not an identification.</b> It is read from the <c>User-Agent</c> once, when the browser
/// subscribes, and never consulted again. A browser that lies about itself mislabels only its own row,
/// and an iPad asking for the desktop site calls itself a Mac - which is what its owner sees it doing
/// everywhere else too.
/// </para>
/// <para>
/// The order of the checks is the substance: Edge, Opera and Samsung's browser all announce Chrome as
/// well, and every Chromium browser announces Safari, so the specific ones are asked first.
/// </para>
/// </remarks>
public static class BrowserNames
{
    /// <summary>
    /// A short name for the browser and platform in <paramref name="userAgent"/>, or
    /// <paramref name="fallback"/> when neither can be told.
    /// </summary>
    public static string FromUserAgent(string? userAgent, string fallback)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return fallback;
        }

        string? browser = Browser(userAgent);
        string? platform = Platform(userAgent);

        return (browser, platform) switch
        {
            (null, null) => fallback,
            (not null, null) => browser,
            (null, not null) => platform,
            _ => $"{browser} · {platform}",
        };
    }

    private static string? Browser(string userAgent)
    {
        if (Has(userAgent, "Edg/") || Has(userAgent, "EdgiOS/") || Has(userAgent, "EdgA/"))
        {
            return "Edge";
        }

        if (Has(userAgent, "OPR/"))
        {
            return "Opera";
        }

        if (Has(userAgent, "SamsungBrowser/"))
        {
            return "Samsung Internet";
        }

        if (Has(userAgent, "Firefox/") || Has(userAgent, "FxiOS/"))
        {
            return "Firefox";
        }

        if (Has(userAgent, "Chrome/") || Has(userAgent, "CriOS/"))
        {
            return "Chrome";
        }

        if (Has(userAgent, "Safari/"))
        {
            return "Safari";
        }

        return null;
    }

    private static string? Platform(string userAgent)
    {
        if (Has(userAgent, "iPhone"))
        {
            return "iPhone";
        }

        if (Has(userAgent, "iPad"))
        {
            return "iPad";
        }

        if (Has(userAgent, "Android"))
        {
            return "Android";
        }

        if (Has(userAgent, "CrOS"))
        {
            return "ChromeOS";
        }

        if (Has(userAgent, "Windows"))
        {
            return "Windows";
        }

        if (Has(userAgent, "Macintosh") || Has(userAgent, "Mac OS X"))
        {
            return "macOS";
        }

        if (Has(userAgent, "Linux"))
        {
            return "Linux";
        }

        return null;
    }

    private static bool Has(string userAgent, string token)
    {
        return userAgent.Contains(token, StringComparison.Ordinal);
    }
}
