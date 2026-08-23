using System.Globalization;
using System.Threading;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

using Homespool.Host.Localisation;
using Homespool.Host.Pages.Printers;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrintDuration"/> - the time left, at the precision the printer reports.
/// </summary>
/// <remarks>
/// <para>
/// The seconds it drops were never the printer's: every one of 27 026 telemetry samples carrying a
/// time remaining held an exact multiple of sixty, so <c>0:04:00</c> ended in two digits that could
/// only be zero. Then the clock face went too, because <c>0:04</c> reads as four seconds.
/// </para>
/// <para>
/// <b>Against a real localiser and the shipped resources</b>, for the reason
/// <see cref="LocalisationTests"/> gives: a stub would pass whether or not the keys exist.
/// </para>
/// </remarks>
public class PrintDurationTests
{
    [Theory]
    [InlineData(240, "4 minutes")]
    [InlineData(0, "0 minutes")]
    [InlineData(60, "1 minute")]
    [InlineData(3600, "1 hour")]
    [InlineData(7200, "2 hours")]
    [InlineData(3660, "1 hour 1 minute")]
    [InlineData(8580, "2 hours 23 minutes")]
    public void SaysHoursAndMinutesWithUnits(int seconds, string expected)
    {
        InEnglish(() => PrintDuration.WithoutSeconds(Localiser(), seconds).Should().Be(expected));
    }

    /// <summary>
    /// Seconds are dropped rather than rounded up. Firmware only ever sends whole minutes, so this
    /// does not arise in practice - it is pinned so a value that did carry them could not silently
    /// gain a minute it has not earned.
    /// </summary>
    [Fact]
    public void TruncatesRatherThanRounding()
    {
        InEnglish(() => PrintDuration.WithoutSeconds(Localiser(), 299).Should().Be("4 minutes"));
    }

    /// <summary>
    /// <b>Hours are not wrapped at a day.</b> A long print is what somebody plans around, and a
    /// two-day estimate restarting from zero would read as two hours.
    /// </summary>
    [Fact]
    public void CountsPastTwentyFourHours()
    {
        InEnglish(() => PrintDuration.WithoutSeconds(Localiser(), 50 * 3600).Should().Be("50 hours"));
    }

    /// <summary>
    /// The units are translated and inflected, which is the whole reason this asks the localiser
    /// rather than composing a string.
    /// </summary>
    [Theory]
    [InlineData(8580, "2 timer 23 minutter")]
    [InlineData(3660, "1 time 1 minut")]
    [InlineData(240, "4 minutter")]
    public void TranslatesAndInflectsTheUnits(int seconds, string expected)
    {
        InCulture("da", () => PrintDuration.WithoutSeconds(Localiser(), seconds).Should().Be(expected));
    }

    private static IStringLocalizer<SharedResource> Localiser()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddLocalization();

        return services.BuildServiceProvider().GetRequiredService<IStringLocalizer<SharedResource>>();
    }

    private static void InEnglish(System.Action assertion)
    {
        InCulture("en-GB", assertion);
    }

    /// <summary>Runs an assertion under a culture, and puts the old one back afterwards.</summary>
    private static void InCulture(string name, System.Action assertion)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo culture = new(name);
            Thread.CurrentThread.CurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;

            assertion();
        }
        finally
        {
            Thread.CurrentThread.CurrentUICulture = previous;
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
