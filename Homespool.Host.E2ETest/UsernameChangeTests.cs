using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

using Homespool.Host.Accounts;
using Homespool.Host.Localisation;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Changing your own username on <c>Account/Manage</c>, through the real page: what is stored, what
/// the header shows afterwards, and what a refusal leaves in the form.
/// </summary>
/// <remarks>
/// Which names are acceptable is the validator's business and is tested there. These tests cover what
/// only the page decides: that a change in capitals alone is a change, that a refusal is shown on the
/// page rather than lost in a redirect, that the typed value is trimmed and prepared before Identity
/// sees it, and that the sign-in cookie is re-issued so the session survives the rename and shows the
/// new name.
/// </remarks>
public sealed class UsernameChangeTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("usernamechange");
    private readonly CapturingSink _logs = new();
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch, extraSinks: [_logs]);

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
    /// A rename is stored, and the page after it says so and shows the new name in the header - still
    /// signed in, although changing the name changes the security stamp the cookie was issued against.
    /// </summary>
    /// <remarks>
    /// Both halves depend on the cookie being re-issued. Renaming changes the security stamp, and a
    /// session still carrying the old stamp is signed out on its next request; and the header reads
    /// the name from the cookie, not the database.
    /// </remarks>
    [Fact]
    public async Task ARenameIsStoredAndTheHeaderShowsIt()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "before@example.com");

        using (client)
        {
            // Act
            HttpResponseMessage response = await PostUsernameAsync(client, "after");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Redirect, "a successful change redirects back to the page");

            (await StoredUsernameAsync(user)).Should().Be("after");

            HttpResponseMessage next = await client.GetAsync("/Account/Manage", TestContext.Current.CancellationToken);
            next.StatusCode.Should().Be(HttpStatusCode.OK, "the rename must not sign the person out");

            string page = await next.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            HeaderName(page).Should().Be("after", "the header renders the name the cookie carries");
            page.Should().Contain(Encoded(Localised("Manage_ProfileUpdated")));
        }
    }

    /// <summary>
    /// <c>henrik</c> to <c>Henrik</c> is a rename. Identity normalises the two to the same name, so a
    /// case-insensitive comparison would skip the change and report success; it changes what every
    /// page renders, so it has to happen.
    /// </summary>
    [Fact]
    public async Task AChangeInCapitalsOnlyIsARename()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "henrik@example.com");

        using (client)
        {
            // Act
            HttpResponseMessage response = await PostUsernameAsync(client, "Henrik");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Redirect,
                                            "the account's own name is not a duplicate of itself, whatever its case");

            (await StoredUsernameAsync(user)).Should().Be("Henrik");

            string page = await client.GetStringAsync("/Account/Manage", TestContext.Current.CancellationToken);
            HeaderName(page).Should().Be("Henrik");
        }
    }

    /// <summary>
    /// A name another account holds - in any case - is refused on the page itself, with Identity's
    /// reason shown and the typed value left in the field to be edited, and the account keeps its name.
    /// </summary>
    [Fact]
    public async Task ATakenUsernameIsRefusedOnThePage()
    {
        // Arrange
        (HSUser _, HttpClient other) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "bob@example.com");
        other.Dispose();

        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "alice@example.com");

        using (client)
        {
            // Act
            HttpResponseMessage response = await PostUsernameAsync(client, "Bob");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                                            "a redirect would carry nothing but a status message, and the reason would be lost");

            string page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            page.Should().Contain(Encoded(DuplicateUserName("Bob")));
            UsernameField(page).Should().Be("Bob", "the typed value stays in the form so it can be edited rather than retyped");

            (await StoredUsernameAsync(user)).Should().Be("alice");
        }
    }

    /// <summary>
    /// The typed value is trimmed and put in its clean form before Identity sees it: surrounding spaces
    /// go, and an accent typed as a separate combining mark is stored composed. The validator refuses
    /// both a space and a decomposed accent, so a rename that skipped either step would be refused.
    /// </summary>
    [Fact]
    public async Task TheTypedNameIsTrimmedAndStoredInItsCleanForm()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "zoe@example.com");

        using (client)
        {
            // Act
            HttpResponseMessage response = await PostUsernameAsync(client, " Zoe\u0308 ");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            (await StoredUsernameAsync(user)).Should().Be("Zo\u00EB");
        }
    }

    /// <summary>
    /// An empty field is refused by the form's own validation, before anything reaches Identity, and
    /// the account keeps its name.
    /// </summary>
    [Fact]
    public async Task ABlankUsernameIsRefused()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "keep@example.com");

        using (client)
        {
            // Act
            HttpResponseMessage response = await PostUsernameAsync(client, string.Empty);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK, "the page is shown again with the field's error");
            (await StoredUsernameAsync(user)).Should().Be("keep");
            _logs.Failures.Should().BeEmpty("an empty field is ordinary input, not a failure");
        }
    }

    private static async Task<HttpResponseMessage> PostUsernameAsync(HttpClient client, string username)
    {
        string form = await client.GetStringAsync("/Account/Manage", TestContext.Current.CancellationToken);

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["Input.Username"] = username,
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(form),
        });

        return await client.PostAsync("/Account/Manage", body, TestContext.Current.CancellationToken);
    }

    /// <summary>The name in the header's account menu, which is read from the sign-in cookie.</summary>
    private static string HeaderName(string page)
    {
        Match match = Regex.Match(page, """<a id="manage"[^>]*>([^<]*)</a>""");
        match.Success.Should().BeTrue("a signed-in page renders the account menu");

        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>The value the form's username field was rendered with.</summary>
    private static string UsernameField(string page)
    {
        Match input = Regex.Match(page, """<input[^>]*name="Input.Username"[^>]*>""");
        input.Success.Should().BeTrue("the page renders the username field");

        Match value = Regex.Match(input.Value, "value=\"([^\"]*)\"");
        value.Success.Should().BeTrue("the username field carries a value");

        return WebUtility.HtmlDecode(value.Groups[1].Value);
    }

    private async Task<string?> StoredUsernameAsync(HSUser user)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
        HSUser fresh = await users.FindByIdAsync(user.Id.ToString(CultureInfo.InvariantCulture)) ??
                       throw new InvalidOperationException("the account should still exist");

        return fresh.UserName;
    }

    /// <summary>The application's own wording for <paramref name="key"/>, so this does not pin English.</summary>
    private string Localised(string key)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResource>>()[key].Value;
    }

    /// <summary>What the registered describer says about a taken name, so this does not pin its wording.</summary>
    private string DuplicateUserName(string userName)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<IdentityErrorDescriber>().DuplicateUserName(userName).Description;
    }

    /// <summary><paramref name="text"/> as the application's own encoder writes it into a page.</summary>
    private string Encoded(string text)
    {
        return _factory.Services.GetRequiredService<HtmlEncoder>().Encode(text);
    }
}
