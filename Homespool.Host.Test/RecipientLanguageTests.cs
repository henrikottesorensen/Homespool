using System;
using System.Globalization;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

using Homespool.Host.Accounts;
using Homespool.Host.Localisation;

namespace Homespool.Host.Test;

/// <summary>
/// Composing for somebody who is not making a request.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the capability the whole of Phase A was justified by, and until now nothing used
/// it.</b> <c>TelemetryAlertService</c> runs on a timer with no <c>HttpContext</c>, so a recipient's
/// language can only come from the stored column — which is why <c>HSUser.Language</c> is a column
/// and not a cookie.
/// </para>
/// <para>
/// The alert service's own send loop is covered by <c>TelemetryAlertMailpitTests</c>, which needs a
/// Mailpit container, and its recipients' languages by <c>AlertRecipientsTests</c>. This covers
/// Identity's one corrected message reading from resources.
/// </para>
/// </remarks>
public sealed class RecipientLanguageTests
{
    /// <summary>
    /// Identity's one corrected message now reads from resources, so a rejection arrives in the
    /// language the page is being rendered in.
    /// </summary>
    /// <remarks>
    /// The wording matters as much as the language: Identity's own text says "letters or digits",
    /// which denies three punctuation marks this application actually accepts. Both translations
    /// have to keep listing them.
    /// </remarks>
    [Fact]
    public void TheCorrectedUsernameMessageIsLocalised()
    {
        HSIdentityErrorDescriber describer = new(Localiser());

        string english = InCulture("en-GB", () => describer.InvalidUserName("henrik@example.com").Description);
        string danish = InCulture("da", () => describer.InvalidUserName("henrik@example.com").Description);

        english.Should().Contain("henrik@example.com").And.Contain("- . _");
        danish.Should().Contain("henrik@example.com").And.Contain("- . _");
        danish.Should().NotBe(english, "the message is localised, not merely formatted");
        danish.Should().StartWith("'henrik@example.com' kan ikke bruges");
    }

    private static IStringLocalizer<SharedResource> Localiser()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddLocalization();

        return services.BuildServiceProvider().GetRequiredService<IStringLocalizer<SharedResource>>();
    }

    private static T InCulture<T>(string cultureName, Func<T> body)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        try
        {
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }
}
