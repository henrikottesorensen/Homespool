using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// That the account-management pages refuse an anonymous caller by rule, and that the sign-in cookie
/// says which cross-site requests carry it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both were true before and neither was stated.</b> Every page under <c>Account/Manage</c> already
/// turned an anonymous caller away, but as a consequence of each handler needing the account rather
/// than as a rule — so a handler that did not happen to need one would have been open, and nothing
/// would have said so. The cookie's <c>SameSite</c> was the framework default rather than a choice,
/// on the setting that is currently the only thing between a cross-site POST and an authenticated
/// <c>/api</c> call.
/// </para>
/// <para>
/// These tests exist so that both are now assertions rather than accidents. Neither changes
/// behaviour; they change what a future edit can quietly undo.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class ManageFolderAuthorizationTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-manageauth-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Every page in the folder, not a sample: the convention is what makes this a rule, and a rule
    /// that held for one page and not its neighbour would be the arrangement it replaced.
    /// </summary>
    [Theory]
    [InlineData("/Account/Manage")]
    [InlineData("/Account/Manage/ApiTokens")]
    [InlineData("/Account/Manage/ChangePassword")]
    [InlineData("/Account/Manage/Disable2fa")]
    [InlineData("/Account/Manage/Email")]
    [InlineData("/Account/Manage/EnableAuthenticator")]
    [InlineData("/Account/Manage/ExternalLogins")]
    [InlineData("/Account/Manage/GenerateRecoveryCodes")]
    [InlineData("/Account/Manage/Language")]
    [InlineData("/Account/Manage/ResetAuthenticator")]
    [InlineData("/Account/Manage/ShowRecoveryCodes")]
    [InlineData("/Account/Manage/TwoFactorAuthentication")]
    public async Task AnAnonymousCallerIsSentToSignIn(string path)
    {
        using HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect,
                                        "the folder is authorized, so an anonymous request is challenged rather than "
                                        + "answered - a 404 here would mean the page refused by accident instead");

        response.Headers.Location!.OriginalString
                .Should().Contain("/Account/Login", "the challenge sends them somewhere they can act on");
    }

    /// <summary>
    /// The other half: a signed-in account still reaches its own pages. An authorization rule that
    /// refused everybody would satisfy the test above.
    /// </summary>
    [Fact]
    public async Task ASignedInAccountStillReachesThem()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "manager@example.com");

        using (client)
        {
            using HttpResponseMessage response =
                await client.GetAsync("/Account/Manage/ApiTokens", TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    /// <summary>
    /// Lax rather than inherited, and rather than Strict. Strict was costed and rejected: it withholds
    /// the cookie on every cross-site top-level navigation, so every emailed link would open the app
    /// signed out.
    /// </summary>
    [Fact]
    public void TheSignInCookieDeclaresItsSameSite()
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        CookieAuthenticationOptions options = scope.ServiceProvider
                                                   .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                                                   .Get(Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme);

        options.Cookie.SameSite.Should().Be(Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                                            "it is the only thing standing between a cross-site POST and an "
                                            + "authenticated /api call, so it should be a decision rather than a default");

        options.Cookie.HttpOnly.Should().BeTrue("script has no business reading a session cookie");

        // Not Always: it would withhold the cookie from every http:// request, so the rig and any
        // deployment run without the proxy could not sign in at all. On the shipped stack the app is
        // told the scheme by X-Forwarded-Proto from a trusted proxy, so this still issues Secure.
        options.Cookie.SecurePolicy.Should().Be(Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest,
                                                "a plaintext deployment is supported rather than tolerated, "
                                                + "and Always would lock it out of sign-in entirely");
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
}
