using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// An account page's status message is coloured by what it reports, in every language, after the
/// redirect that carries it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The GET after the redirect is the test.</b> The kind travels in TempData beside the message,
/// and TempData's serializer reads values back by their shape - a property it cannot assign fails
/// there, on the next page, never on the post that set it.
/// </para>
/// <para>
/// <b>Danish is the case that matters.</b> Colouring by the message's own wording can only ever
/// match one language, so a check that passes in English proves nothing about the other.
/// </para>
/// </remarks>
public sealed partial class StatusMessageKindTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("status-kind");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);
        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }

    /// <summary>
    /// Revoking a token that is not there is information, not a success - in either language.
    /// </summary>
    [Theory]
    [InlineData(null, "That token no longer exists.")]
    [InlineData("da", "Dette token findes ikke")]
    public async Task AnAlreadyGoneTokenIsShownAsInformation(string? language, string expected)
    {
        // Arrange
        (HSUser _, HttpClient client) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, $"gone-{language ?? "en"}@example.com");

        using (client)
        {
            if (language is not null)
            {
                await ChooseLanguageAsync(client, language);
            }

            string opened = await GetStringAsync(client, "/Account/Manage/ApiTokens");

            // Act
            using (HttpResponseMessage revoked = await PostAsync(client,
                                                                 "/Account/Manage/ApiTokens?handler=Revoke",
                                                                 opened,
                                                                 KeyValuePair.Create("uuid", Guid.NewGuid().ToString())))
            {
                revoked.StatusCode.Should().Be(HttpStatusCode.Redirect, "the message rides TempData across the redirect");
            }

            string page = await GetStringAsync(client, "/Account/Manage/ApiTokens");

            // Assert
            page.Should().Contain(expected);
            AlertClasses(page).Should().Equal(["alert-info"], "the one status alert on the page is this message's");
        }
    }

    /// <summary>A success still reads as one, after the same redirect.</summary>
    [Fact]
    public async Task ASuccessIsShownAsASuccess()
    {
        // Arrange
        (HSUser _, HttpClient client) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "forgets@example.com");

        using (client)
        {
            string opened = await GetStringAsync(client, "/Account/Manage/TwoFactorAuthentication");

            // Act
            using (HttpResponseMessage forgotten = await PostAsync(client, "/Account/Manage/TwoFactorAuthentication", opened))
            {
                forgotten.StatusCode.Should().Be(HttpStatusCode.Redirect);
            }

            string page = await GetStringAsync(client, "/Account/Manage/TwoFactorAuthentication");

            // Assert
            AlertClasses(page).Should().Equal(["alert-success"]);
        }
    }

    /// <summary>
    /// Stores the account's language, then reads the page that confirms it, so the confirmation is
    /// spent before the test's own message is set.
    /// </summary>
    private static async Task ChooseLanguageAsync(HttpClient client, string language)
    {
        string page = await GetStringAsync(client, "/Account/Manage/Language");

        using (HttpResponseMessage saved = await PostAsync(client, "/Account/Manage/Language", page, KeyValuePair.Create("Selected", language)))
        {
            saved.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }

        AlertClasses(await GetStringAsync(client, "/Account/Manage/Language")).Should().Equal(["alert-success"]);
    }

    /// <summary>The contextual class of every alert the status partial rendered, in page order.</summary>
    private static List<string> AlertClasses(string page)
    {
        List<string> classes = [];

        foreach (Match match in StatusAlert().Matches(page))
        {
            classes.Add(match.Groups[1].Value);
        }

        return classes;
    }

    private static async Task<string> GetStringAsync(HttpClient client, string path)
    {
        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A post carrying the antiforgery token from <paramref name="page"/> and any further fields.</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client,
                                                             string path,
                                                             string page,
                                                             params KeyValuePair<string, string>[] fields)
    {
        List<KeyValuePair<string, string>> form = [new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)), .. fields];

        using FormUrlEncodedContent content = new(form);

        return await client.PostAsync(path, content, TestContext.Current.CancellationToken);
    }

    [GeneratedRegex("""<div class="alert (alert-[a-z]+) alert-dismissible" role="alert">""")]
    private static partial Regex StatusAlert();
}
