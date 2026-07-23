using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PrinterService.Host.PrusaConnect;
using PrinterService.Host.Services;
using PrinterService.Model.Entities;

namespace PrinterService.Host.E2ETest;

/// <summary>
/// The printer registration / claim / poll steps shared by every test that needs to drive that flow
/// through the real HTTP pipeline rather than seeding rows directly - extracted from
/// <see cref="EndToEndEnrollmentTests"/> once <see cref="PrusaConnectWebSocketTests"/> needed the
/// identical steps to obtain a genuinely valid Fingerprint/Token pair.
/// </summary>
public static class EnrollmentFlowHelper
{
    public static async Task<HttpResponseMessage> SendPrinterRegisterAsync(HttpClient client, object body)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/p/register") { Content = JsonContent.Create(body) };

        return await client.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> SendPollAsync(HttpClient client, string code)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/p/register");
        request.Headers.Add(Headers.Code, code);

        return await client.SendAsync(request);
    }

    /// <summary>
    /// Seeds an ordinary account with its default team - the same creation dance
    /// <c>Setup.cshtml.cs</c>/<c>Register.cshtml.cs</c> perform - and mints a cookie for it via the
    /// exact <see cref="CookieAuthenticationOptions.TicketDataFormat"/> real sign-in would use, so the
    /// server validates it through the genuine cookie-auth pipeline. Bypasses the Login page's
    /// antiforgery-protected form, which isn't what these suites are testing.
    /// </summary>
    public static async Task<(PSUser User, HttpClient Client)> CreateAuthenticatedUserAsync(PrinterServiceFactory factory, string email)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        IUserStore<PSUser> userStore = scope.ServiceProvider.GetRequiredService<IUserStore<PSUser>>();
        IUserEmailStore<PSUser> emailStore = (IUserEmailStore<PSUser>)userStore;
        UserManager<PSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<PSUser>>();
        SignInManager<PSUser> signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<PSUser>>();
        AccountConfirmationPolicy confirmationPolicy = scope.ServiceProvider.GetRequiredService<AccountConfirmationPolicy>();
        TeamService teamService = scope.ServiceProvider.GetRequiredService<TeamService>();

        PSUser user = new();
        await userStore.SetUserNameAsync(user, email, CancellationToken.None);
        await emailStore.SetEmailAsync(user, email, CancellationToken.None);
        confirmationPolicy.Apply(user);

        IdentityResult createResult = await userManager.CreateAsync(user, "Correct-Horse-Battery-Staple-1!");
        createResult.Succeeded.Should().BeTrue("account creation is setup for this test, not what it verifies");

        await teamService.AddDefaultTeamAsync(user.Id, DateTimeOffset.UtcNow, CancellationToken.None);

        ClaimsPrincipal principal = await signInManager.CreateUserPrincipalAsync(user);
        CookieAuthenticationOptions cookieOptions = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        AuthenticationTicket ticket = new(principal, IdentityConstants.ApplicationScheme);
        string protectedTicket = cookieOptions.TicketDataFormat.Protect(ticket);

        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{cookieOptions.Cookie.Name}={protectedTicket}");

        return (user, client);
    }
}
