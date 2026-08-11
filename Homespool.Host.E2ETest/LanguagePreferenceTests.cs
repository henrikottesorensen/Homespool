using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Services;
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
