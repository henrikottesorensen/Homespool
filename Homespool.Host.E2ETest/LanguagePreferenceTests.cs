using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Choosing a language, driven the way a browser drives it.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are the tests the unit ones cannot be.</b> <c>LocalisationTests</c> sets a culture
/// directly and asserts the resources answer; nothing there touches the middleware, so nothing
/// there would notice <c>UseRequestLocalization</c> being absent, registered in the wrong order, or
/// reading providers that never fire. That ordering is the one piece of this work with a silent
/// failure mode — the account provider needs <c>HttpContext.User</c>, so placed before
/// <c>UseAuthentication</c> it sees an anonymous request every time and quietly does nothing.
/// </para>
/// <para>
/// Asserted against the navigation label rather than the page heading, because the nav is rendered
/// by a shared partial on every page in the section: if it is Danish, the culture reached the whole
/// render and not just the one view under test.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class LanguagePreferenceTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-language-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _factory.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// The whole point, end to end: pick Danish, and the next page is Danish.
    /// </summary>
    [Fact]
    public async Task ChoosingDanishChangesTheLanguageOfTheNextPage()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "language-choose@example.com");

        string before = await GetStringAsync(client, "/Account/Manage/Language");
        before.Should().Contain("Use my browser’s language setting", "an account with no stored language starts unset");
        before.Should().Contain("Language", "and reads in the default language");

        // Act
        HttpResponseMessage saved = await PostLanguageAsync(client, before, "da");

        // Assert
        saved.StatusCode.Should().Be(HttpStatusCode.Redirect);

        string after = await GetStringAsync(client, "/Account/Manage/Language");

        after.Should().Contain("Sprog", "the stored preference decides the culture of every later request");
        after.Should().Contain("Brug browserens sprogindstilling");
        after.Should().Contain("Sproget er opdateret.", "the confirmation is written in the language just chosen");
        after.Should().NotContain("Use my browser’s language setting");

        client.Dispose();
    }

    /// <summary>
    /// A stored choice beats the browser, which is the reason it is stored rather than negotiated.
    /// </summary>
    /// <remarks>
    /// The case a picker exists for: signing in from a machine whose browser asks for English, having
    /// chosen Danish. If the provider order were reversed - or the account provider never fired - this
    /// would come back English and nothing else in the suite would notice.
    /// </remarks>
    [Fact]
    public async Task AStoredChoiceBeatsTheBrowsersHeader()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "language-beats-header@example.com");

        string page = await GetStringAsync(client, "/Account/Manage/Language");
        await PostLanguageAsync(client, page, "da");

        using HttpRequestMessage asking = new(HttpMethod.Get, "/Account/Manage/Language");
        asking.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-GB"));

        using HttpResponseMessage response = await client.SendAsync(asking, TestContext.Current.CancellationToken);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        html.Should().Contain("Sprog");
        html.Should().NotContain("Use my browser’s language setting");

        client.Dispose();
    }

    /// <summary>
    /// With nothing stored, the browser decides — so "follow my browser" is a real behaviour rather
    /// than a label.
    /// </summary>
    [Fact]
    public async Task WithNothingStoredTheBrowsersHeaderDecides()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "language-header@example.com");

        using HttpRequestMessage asking = new(HttpMethod.Get, "/Account/Manage/Language");
        asking.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("da-DK"));

        using HttpResponseMessage response = await client.SendAsync(asking, TestContext.Current.CancellationToken);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        html.Should().Contain("Sprog", "da-DK selects the neutral Danish that is actually shipped");

        client.Dispose();
    }

    /// <summary>
    /// A language Homespool does not ship falls back to the default rather than half-applying.
    /// </summary>
    [Fact]
    public async Task AnUnshippedLanguageFallsBackToTheDefault()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "language-unshipped@example.com");

        using HttpRequestMessage asking = new(HttpMethod.Get, "/Account/Manage/Language");
        asking.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("de-DE"));

        using HttpResponseMessage response = await client.SendAsync(asking, TestContext.Current.CancellationToken);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        html.Should().Contain("Use my browser’s language setting");
        html.Should().NotContain("Sprog");

        client.Dispose();
    }

    /// <summary>
    /// Going back to "follow my browser" has to clear the column, not store an empty string that
    /// matches no language.
    /// </summary>
    [Fact]
    public async Task ChoosingToFollowTheBrowserAgainClearsTheStoredChoice()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "language-clear@example.com");

        string page = await GetStringAsync(client, "/Account/Manage/Language");
        await PostLanguageAsync(client, page, "da");

        string danish = await GetStringAsync(client, "/Account/Manage/Language");
        danish.Should().Contain("Sprog");

        await PostLanguageAsync(client, danish, string.Empty);

        using HttpRequestMessage asking = new(HttpMethod.Get, "/Account/Manage/Language");
        asking.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-GB"));

        using HttpResponseMessage response = await client.SendAsync(asking, TestContext.Current.CancellationToken);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        html.Should().Contain("Use my browser’s language setting", "clearing the choice hands the decision back to the browser");

        client.Dispose();
    }

    /// <summary>
    /// A converted page renders in the chosen language, headings and controls alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The picker proving it switches is not the same as a page proving it was converted. This asks
    /// for a real working page - the printer list - and checks the parts a person actually reads:
    /// the heading, a column heading, the empty-state sentence and a button.
    /// </para>
    /// <para>
    /// <b>It also asserts what stays English.</b> Nothing here should translate a CSS class, and
    /// <c>text-bg-secondary</c> appearing intact is the cheapest available check that the
    /// machine-text boundary survived the conversion of this page.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AConvertedPageRendersInTheChosenLanguage()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "language-printers@example.com");

        string english = await GetStringAsync(client, "/Printers");
        english.Should().Contain("No printers yet.");
        english.Should().Contain("Claim printer (code)");

        string page = await GetStringAsync(client, "/Account/Manage/Language");
        await PostLanguageAsync(client, page, "da");

        string danish = await GetStringAsync(client, "/Printers");

        danish.Should().Contain("Printere");
        danish.Should().Contain("Ingen printere endnu.");
        danish.Should().Contain("Tilknyt printer (kode)");
        danish.Should().Contain("Tilføj printer (USB-nøgle)");
        danish.Should().NotContain("No printers yet.");

        client.Dispose();
    }

    /// <summary>
    /// Form labels and placeholders come from the resources too, and fail visibly when they do not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A <c>[Display(Name = "…")]</c> holding a resource key has one failure mode worth a test of
    /// its own: with <c>AddDataAnnotationsLocalization</c> unregistered, misconfigured, or pointed at
    /// a different resource type, the key renders as the label.</b> The page still returns 200, every
    /// other assertion in this file still passes, and the form says <c>Manage_CurrentPassword</c> to
    /// whoever opens it. Asserting the key is <i>absent</i> is what catches that, in either language.
    /// </para>
    /// <para>
    /// The placeholder is checked alongside because it travels the other route - an ordinary
    /// localiser lookup in the view - and the two are easy to get out of step now that the label is
    /// no longer written next to it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FormLabelsAndPlaceholdersAreLocalisedRatherThanShowingTheirKeys()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "language-labels@example.com");

        string english = await GetStringAsync(client, "/Account/Manage/ChangePassword");
        english.Should().Contain("Current password");
        english.Should().Contain("Please enter your new password.");
        english.Should().NotContain("Manage_CurrentPassword", "the key is a lookup, never a label");

        string page = await GetStringAsync(client, "/Account/Manage/Language");
        await PostLanguageAsync(client, page, "da");

        string danish = await GetStringAsync(client, "/Account/Manage/ChangePassword");
        danish.Should().Contain("Nuværende adgangskode");
        danish.Should().Contain("Indtast din nye adgangskode.");
        danish.Should().NotContain("Manage_CurrentPassword");
        danish.Should().NotContain("Current password");

        client.Dispose();
    }

    /// <summary>
    /// The document says which language it is in, and says the one it was rendered in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one localisation failure a reader cannot see and a listener cannot avoid.</b> Nothing
    /// on screen changes when <c>&lt;html&gt;</c> carries no <c>lang</c>: the page is perfectly
    /// Danish, every other test in this file passes, and a screen reader reads it aloud in whatever
    /// voice it defaults to - which for Danish text in an English voice is not an accent but noise.
    /// </para>
    /// <para>
    /// Asserted from the stored preference as well as from the header, because the attribute has to
    /// follow the culture that was actually resolved rather than the request that suggested one - the
    /// same distinction <see cref="AStoredChoiceBeatsTheBrowsersHeader"/> makes for the words.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheDocumentDeclaresTheLanguageItWasRenderedIn()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "language-declared@example.com");

        (await AskInAsync(client, "en-GB")).Should().Contain("<html lang=\"en\">");
        (await AskInAsync(client, "da-DK")).Should().Contain("<html lang=\"da\">");

        string page = await GetStringAsync(client, "/Account/Manage/Language");
        await PostLanguageAsync(client, page, "da");

        (await AskInAsync(client, "en-GB")).Should().Contain("<html lang=\"da\">",
                                                             "the attribute follows the resolved culture, not the header that lost");

        client.Dispose();
    }

    private static async Task<string> AskInAsync(HttpClient client, string language)
    {
        using HttpRequestMessage asking = new(HttpMethod.Get, "/Account/Manage/Language");
        asking.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(language));

        using HttpResponseMessage response = await client.SendAsync(asking, TestContext.Current.CancellationToken);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> GetStringAsync(HttpClient client, string path)
    {
        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> PostLanguageAsync(HttpClient client, string page, string selected)
    {
        List<KeyValuePair<string, string>> form =
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
            new("Selected", selected),
        ];

        using FormUrlEncodedContent content = new(form);

        return await client.PostAsync("/Account/Manage/Language", content, TestContext.Current.CancellationToken);
    }
}
