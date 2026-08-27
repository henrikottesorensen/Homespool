using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Localisation;
using Homespool.Model.Entities;

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
/// Mailpit container. These cover the two pieces that do not: resolving a language from an address,
/// and Identity's one corrected message reading from resources.
/// </para>
/// </remarks>
public sealed class RecipientLanguageTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"hs-recipient-language-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// An address, not a user id - because that is all a sender has.
    /// </summary>
    [Fact]
    public async Task ARecipientsLanguageIsFoundByTheirAddress()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        UserCultures cultures = new(context);

        await AddUserAsync(context, "dane@example.com", "da");
        await AddUserAsync(context, "brit@example.com", null);

        (await cultures.ForEmailAsync("dane@example.com", TestContext.Current.CancellationToken))
            .Should().Be("da");

        (await cultures.ForEmailAsync("brit@example.com", TestContext.Current.CancellationToken))
            .Should().BeNull("nobody chose, so the caller falls back rather than being told English");

        (await cultures.ForEmailAsync("nobody@example.com", TestContext.Current.CancellationToken))
            .Should().BeNull("an address with no account is not an error, just nothing to go on");
    }

    /// <summary>
    /// Casing must not decide somebody's language, which is why the lookup is on the normalised
    /// address rather than the one that was typed.
    /// </summary>
    [Fact]
    public async Task TheAddressIsMatchedRegardlessOfCasing()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        UserCultures cultures = new(context);

        await AddUserAsync(context, "Mixed.Case@Example.COM", "da");

        (await cultures.ForEmailAsync("mixed.case@example.com", TestContext.Current.CancellationToken))
            .Should().Be("da");
    }

    /// <summary>
    /// A stored culture that is no longer shipped degrades to the default rather than throwing on
    /// every email to whoever had selected it.
    /// </summary>
    [Fact]
    public async Task ALanguageNoLongerShippedReadsAsNoChoice()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        UserCultures cultures = new(context);

        await AddUserAsync(context, "german@example.com", "de-DE");

        (await cultures.ForEmailAsync("german@example.com", TestContext.Current.CancellationToken))
            .Should().BeNull();
    }

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

    private static async Task AddUserAsync(HomespoolDbContext context, string email, string? language)
    {
        context.Users.Add(new HSUser(email.Split('@')[0].Replace('.', '-'))
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            Language = language,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        HomespoolDbContext context = new(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }
}
