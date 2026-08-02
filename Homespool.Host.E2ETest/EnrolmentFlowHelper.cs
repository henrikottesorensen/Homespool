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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.FakePrinter;
using Homespool.Host.Controllers;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The printer registration / claim / poll steps shared by every test that needs to drive that flow
/// through the real HTTP pipeline rather than seeding rows directly - extracted from
/// <see cref="EndToEndEnrolmentTests"/> once <see cref="PrusaConnectWebSocketTests"/> needed the
/// identical steps to obtain a genuinely valid Fingerprint/Token pair.
/// </summary>
public static class EnrolmentFlowHelper
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
    /// The one definition of "an enrolled printer" for tests that need one as <em>setup</em>: mints
    /// a random <see cref="PrinterIdentity"/>, registers it through the real code-exchange flow,
    /// claims it as a freshly seeded user, and polls the real token out. Credentials can only come
    /// from that chain - tokens are PBKDF2-hashed, so they cannot be recovered from the database -
    /// which is also what makes them genuinely valid against the auth handler afterwards.
    /// </summary>
    /// <remarks>
    /// Extracted when <c>FakePrinterIntegrationTests</c> became the second class to reimplement
    /// <c>PrusaConnectWebSocketTests</c>' private enroll-and-claim, so the two could not drift on
    /// what "enrolled" means. Tests whose <em>subject</em> is the raw enrolment HTTP contract
    /// (<see cref="EndToEndEnrolmentTests"/>) deliberately do not use this - they drive
    /// <see cref="SendPrinterRegisterAsync"/>/<see cref="SendPollAsync"/> directly.
    /// </remarks>
    /// <returns>
    /// The identity (its <see cref="PrinterIdentity.HeaderFingerprint"/> is what a real upgrade
    /// presents), the issued token, the claimed printer's id, and the claiming user's id.
    /// </returns>
    public static async Task<(PrinterIdentity identity, string token, int printerId, long userId)> EnrolAndClaimFakePrinterAsync(
        WebApplicationFactory<PrinterAppController> factory)
    {
        PrinterIdentity identity = PrinterIdentity.CreateRandom();
        await using FakePrinterClient enrolling = new(identity, TimeProvider.System);
        using HttpClient anonymous = PrinterListener.CreateClient(factory);

        string code = await enrolling.RegisterAsync(anonymous);

        (HSUser user, HttpClient appClient) = await CreateAuthenticatedUserAsync(
            factory, $"{identity.HeaderFingerprint}@example.com");

        using (appClient)
        {
            HttpResponseMessage claim = await appClient.PostAsJsonAsync(
                "/api/v1/printers/register",
                new { name = "Fake printer", location = "Test bench", code });
            claim.EnsureSuccessStatusCode();
        }

        string? token = await enrolling.PollForTokenOnceAsync(anonymous, code);
        token.Should().NotBeNull("the claim just happened, so the poll must redeem the code");

        using IServiceScope scope = factory.Services.CreateScope();
        HSDbContext context = scope.ServiceProvider.GetRequiredService<HSDbContext>();
        PrusaConnectAuthenticationData auth = await context.PrusaConnectAuthentication
            .Include(a => a.Printer)
            .SingleAsync(a => a.FingerPrintKey == PrinterFingerprint.Key(identity.Fingerprint));

        return (identity, token!, auth.Printer!.Id, user.Id);
    }

    /// <summary>
    /// Seeds an ordinary account with its default team - the same creation dance
    /// <c>Setup.cshtml.cs</c>/<c>Register.cshtml.cs</c> perform - and mints a cookie for it via the
    /// exact <see cref="CookieAuthenticationOptions.TicketDataFormat"/> real sign-in would use, so the
    /// server validates it through the genuine cookie-auth pipeline. Bypasses the Login page's
    /// antiforgery-protected form, which isn't what these suites are testing.
    /// </summary>
    /// <param name="factory">The application under test.</param>
    /// <param name="email">The account to create; also its user name.</param>
    /// <param name="role">
    /// A role to grant <em>before</em> the cookie is minted. It has to be before: the principal is
    /// built from the user as it stands, so a role added afterwards is absent from the ticket the
    /// server validates, and the test sees an ordinary user with no explanation.
    /// </param>
    public static async Task<(HSUser user, HttpClient client)> CreateAuthenticatedUserAsync(
        WebApplicationFactory<PrinterAppController> factory, string email, string? role = null)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        IUserStore<HSUser> userStore = scope.ServiceProvider.GetRequiredService<IUserStore<HSUser>>();
        IUserEmailStore<HSUser> emailStore = (IUserEmailStore<HSUser>)userStore;
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
        SignInManager<HSUser> signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<HSUser>>();
        AccountConfirmationPolicy confirmationPolicy = scope.ServiceProvider.GetRequiredService<AccountConfirmationPolicy>();
        TeamService teamService = scope.ServiceProvider.GetRequiredService<TeamService>();

        HSUser user = new();
        await userStore.SetUserNameAsync(user, email, CancellationToken.None);
        await emailStore.SetEmailAsync(user, email, CancellationToken.None);
        confirmationPolicy.Apply(user);

        IdentityResult createResult = await userManager.CreateAsync(user, "Correct-Horse-Battery-Staple-1!");
        createResult.Succeeded.Should().BeTrue("account creation is setup for this test, not what it verifies");

        await teamService.AddDefaultTeamAsync(user.Id, DateTimeOffset.UtcNow, CancellationToken.None);

        if (role is not null)
        {
            IdentityResult roleResult = await userManager.AddToRoleAsync(user, role);
            roleResult.Succeeded.Should().BeTrue("the role is setup for this test, not what it verifies");
        }

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
