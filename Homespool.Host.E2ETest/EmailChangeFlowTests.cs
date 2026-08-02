using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Changing your own email address, end to end: request the change, follow the confirmation link,
/// and have the new address actually take effect.
/// </summary>
/// <remarks>
/// Written after finding the flow could not work at all. <c>Account/Manage/Email</c> mailed a link
/// to <c>/Account/ConfirmEmailChange</c>, a page that did not exist, and nothing in the codebase
/// ever called <see cref="UserManager{TUser}.ChangeEmailAsync"/> - so the address could never
/// change however far a user got. Almost certainly missed when the Identity.UI package was removed
/// and its pages were reimplemented locally.
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class EmailChangeFlowTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-emailchange-{Guid.NewGuid():N}.db");
    private readonly CapturingSink _logs = new();
    private HomespoolFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}", extraSinks: [_logs]);

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Dispose();

        return Task.CompletedTask;
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
    /// Requesting a change gets as far as sending a confirmation link, without the handler throwing.
    /// </summary>
    /// <remarks>
    /// The handler builds its link with <c>Url.Page("/Account/ConfirmEmailChange", ...)</c>, which
    /// returns null when no such page is routable, and hands the result straight to
    /// <c>HtmlEncoder.Encode</c>. So a missing page does not produce a dead link in an email - it
    /// throws, and the user gets an error page instead.
    /// </remarks>
    [Fact]
    public async Task RequestingAnEmailChangeSendsAConfirmationLink()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "changer@example.com");

        using (client)
        {
            HttpResponseMessage pageResponse = await client.GetAsync("/Account/Manage/Email");

            pageResponse.StatusCode.Should().Be(HttpStatusCode.OK,
                "every page under Account/Manage rendered a 500 while its layout pointed at the "
              + "Identity.UI file that was removed with the package");

            string page = await pageResponse.Content.ReadAsStringAsync();
            string token = AntiforgeryTestHelper.ExtractToken(page);

            // Act
            using FormUrlEncodedContent body = new(new Dictionary<string, string>
            {
                ["Input.NewEmail"] = "changed@example.com",
                ["__RequestVerificationToken"] = token,
            });

            HttpResponseMessage response = await client.PostAsync("/Account/Manage/Email?handler=ChangeEmail", body);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Redirect,
                "a successful request redirects back to the page with a status message");

            _logs.Failures.Should().BeEmpty("building the confirmation link must not throw");
        }
    }

    /// <summary>
    /// The confirmation link actually applies the new address - the half that never existed.
    /// </summary>
    [Fact]
    public async Task ConfirmingTheChangeAppliesTheNewAddress()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "before@example.com");

        using (client)
        {
            string page = await client.GetStringAsync("/Account/Manage/Email");
            string token = AntiforgeryTestHelper.ExtractToken(page);

            using FormUrlEncodedContent body = new(new Dictionary<string, string>
            {
                ["Input.NewEmail"] = "after@example.com",
                ["__RequestVerificationToken"] = token,
            });

            await client.PostAsync("/Account/Manage/Email?handler=ChangeEmail", body);

            // The token the emailed link would carry. Generated here rather than scraped out of the
            // captured mail, so this test covers the confirmation page rather than the formatting of
            // the message that links to it.
            string confirmUrl = await BuildConfirmUrlAsync(user.Id, "after@example.com");

            // Act
            HttpResponseMessage confirm = await client.GetAsync(confirmUrl);

            // Assert
            confirm.StatusCode.Should().Be(HttpStatusCode.OK);

            using IServiceScope scope = _factory.Services.CreateScope();
            UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
            HSUser reloaded = (await users.FindByIdAsync(user.Id.ToString()))!;

            reloaded.Email.Should().Be("after@example.com", "confirming the link is what makes the change real");
            reloaded.UserName.Should().Be("after@example.com", "the username tracks the email, or the user cannot sign in afterwards");
        }
    }

    /// <summary>
    /// Every page under Account/Manage renders.
    /// </summary>
    /// <remarks>
    /// The broken email change turned out to be one symptom of a broken section: Manage/_Layout
    /// fell back to <c>/Areas/Identity/Pages/_Layout.cshtml</c>, which the Identity.UI package
    /// supplied and which vanished with it. Nothing sets <c>ParentLayout</c>, so every one of these
    /// pages took that branch and returned a 500 - password changes and two-factor setup included.
    /// A per-page check, because one page rendering proves only that one page's layout resolved.
    /// </remarks>
    [Theory]
    [InlineData("/Account/Manage/Index")]
    [InlineData("/Account/Manage/Email")]
    [InlineData("/Account/Manage/ChangePassword")]
    [InlineData("/Account/Manage/TwoFactorAuthentication")]
    public async Task ManagePagesRender(string path)
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, $"manage{path.GetHashCode(StringComparison.Ordinal):X}@example.com");

        using (client)
        {
            // Act
            HttpResponseMessage response = await client.GetAsync(path);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            _logs.Failures.Should().BeEmpty($"{path} should render without anything failing behind it");
        }
    }

    private async Task<string> BuildConfirmUrlAsync(long userId, string newEmail)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
        HSUser user = (await users.FindByIdAsync(userId.ToString()))!;

        string code = await users.GenerateChangeEmailTokenAsync(user, newEmail);
        code = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(code));

        return $"/Account/ConfirmEmailChange?userId={userId}&email={Uri.EscapeDataString(newEmail)}&code={code}";
    }
}
