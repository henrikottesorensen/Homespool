using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace Homespool.Host.Localisation;

/// <summary>
/// Wires up localisation, so <c>Program.cs</c> says what is being added rather than how.
/// </summary>
/// <remarks>
/// The same arrangement <c>Cameras/Registration.cs</c> uses: the configuration for a thing lives
/// beside the thing.
/// </remarks>
public static class Registration
{
    /// <summary>
    /// Adds the resource localiser, the supported cultures and the request-culture pipeline.
    /// </summary>
    public static IServiceCollection AddHomespoolLocalisation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // No ResourcesPath: the .resx files sit beside SharedResource, so the manifest name is the
        // type's own full name and there is no path convention to get subtly wrong.
        services.AddLocalization();

        services.Configure<RequestLocalizationOptions>(ConfigureCultures);

        // Scoped because it reads the request's DbContext.
        services.AddScoped<UserCultures>();

        // Scoped for no reason of its own - it holds a scoped localiser and nothing else.
        services.AddScoped<PrinterStatusText>();

        return services;
    }

    /// <summary>
    /// The supported cultures, and the order in which a request's culture is decided.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The provider order is the whole design.</b> A stored account preference is a decision and
    /// goes first; a cookie is this browser's memory of one and goes second, so a signed-out page
    /// still remembers what the last person here picked; <c>Accept-Language</c> is a guess made by
    /// whoever installed the browser and goes last. The default query-string provider is removed
    /// entirely — <c>?culture=</c> is a debugging affordance that turns every link into something
    /// that can carry a language, and a shared URL should not change the recipient's.
    /// </para>
    /// <para>
    /// <b><c>SetDefaultCulture</c> and <c>SupportedCultures</c> are both set, and they are different
    /// questions.</b> <c>SupportedCultures</c> decides number and date formatting;
    /// <c>SupportedUICultures</c> decides which resources are read. Homespool ships them as the same
    /// list on purpose: a deployment offering Danish text with British dates would be stating a
    /// region nobody chose.
    /// </para>
    /// </remarks>
    private static void ConfigureCultures(RequestLocalizationOptions options)
    {
        IList<CultureInfo> cultures = SupportedLanguages.Cultures.ToList();

        options.DefaultRequestCulture = new RequestCulture(SupportedLanguages.DefaultCulture);
        options.SupportedCultures = cultures;
        options.SupportedUICultures = cultures;

        // A specific request culture falls back to its neutral parent rather than to the default,
        // so da-DK reads the da resources instead of dropping to English.
        options.FallBackToParentCultures = true;
        options.FallBackToParentUICultures = true;

        options.RequestCultureProviders =
        [
            new UserPreferenceCultureProvider(),
            new CookieRequestCultureProvider(),
            new AcceptLanguageHeaderRequestCultureProvider(),
        ];
    }
}
