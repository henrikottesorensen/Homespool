using System;
using System.Globalization;

using Homespool.Host.Localisation;

namespace Homespool.Host.Test;

/// <summary>
/// Stands in for the culture the localisation middleware would have resolved, for a page model called
/// directly, and puts the thread's own back on dispose.
/// </summary>
/// <remarks>
/// <para>
/// <b>For a test that says "the request is English"</b> and means it. Left alone, the request culture
/// in a page-model test is whatever the machine running it is set to, so a test asserting that a mail
/// ignored the request's language would pass or fail by where it ran - and on a Danish machine would
/// pass with the bug in place.
/// </para>
/// <para>
/// Both cultures are set, as the middleware sets both. Set at the top of an async test, the change
/// flows into everything the test awaits and ends with the test.
/// </para>
/// </remarks>
internal sealed class RequestCulture : IDisposable
{
    private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;

    private RequestCulture(string cultureName)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>A request that arrived in British English, the deployment default.</summary>
    public static RequestCulture English()
    {
        return new RequestCulture(SupportedLanguages.DefaultCulture);
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _previousCulture;
        CultureInfo.CurrentUICulture = _previousUiCulture;
    }
}
