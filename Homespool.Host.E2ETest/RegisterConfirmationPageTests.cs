using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Drives <c>/Account/RegisterConfirmation</c> anonymously over HTTP. The page takes an arbitrary
/// address as a query parameter, so its one security property is that it answers identically whether
/// or not that address belongs to an account - an anonymous, unthrottled 404-on-unknown would be an
/// account-existence oracle, defeating the enumeration defences on the login and forgot-password
/// flows.
/// </summary>
public sealed class RegisterConfirmationPageTests : IAsyncLifetime
{
    private const string Password = "Correct-Horse-Battery-Staple-1!";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("regconf");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);
        _ = _factory.Server;

        // This suite isn't testing the setup gate - open it so the Account pages are reachable.
        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }

    // CA1001 wants IDisposable on a type owning a disposable field even though xUnit's IAsyncLifetime
    // already drives cleanup via DisposeAsync above; WebApplicationFactory.Dispose is idempotent, so
    // this is a safe, redundant satisfier rather than a second real teardown path.

    /// <summary>
    /// Seeds an account directly via <see cref="UserManager{TUser}"/>; this suite verifies the
    /// confirmation page's rendering, not account creation.
    /// </summary>
    private async Task CreateUserAsync(string email)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        IUserStore<HSUser> userStore = scope.ServiceProvider.GetRequiredService<IUserStore<HSUser>>();
        IUserEmailStore<HSUser> emailStore = (IUserEmailStore<HSUser>)userStore;
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        HSUser user = new();
        await userStore.SetUserNameAsync(user, EnrolmentFlowHelper.UsernameFor(email), CancellationToken.None);
        await emailStore.SetEmailAsync(user, email, CancellationToken.None);

        IdentityResult result = await userManager.CreateAsync(user, Password);
        result.Succeeded.Should().BeTrue("account creation is setup for this test, not what it verifies");
    }

    /// <summary>
    /// Antiforgery tokens are minted per response, so two renders of the same page differ in exactly
    /// that field; blank it out so the comparison below is over what the page actually says.
    /// </summary>
    private static string WithoutAntiforgeryTokens(string html)
    {
        return Regex.Replace(html, """(<input[^>]*name="__RequestVerificationToken"[^>]*value=")[^"]*(")""", "$1$2");
    }

    /// <summary>
    /// The enumeration pin: an address nobody holds gets the very same page as a registered one -
    /// same status, same body - so the query parameter cannot be used to probe which accounts exist.
    /// </summary>
    [Fact]
    public async Task AnAddressNobodyHoldsRendersExactlyWhatARegisteredOneDoes()
    {
        // Arrange
        await CreateUserAsync("registered@example.com");

        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        HttpResponseMessage knownResponse = await client.GetAsync(
            "/Account/RegisterConfirmation?email=registered@example.com", TestContext.Current.CancellationToken);
        HttpResponseMessage unknownResponse = await client.GetAsync(
            "/Account/RegisterConfirmation?email=nobody@example.com", TestContext.Current.CancellationToken);

        // Assert
        knownResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        unknownResponse.StatusCode.Should().Be(HttpStatusCode.OK, "an unknown address must not be distinguishable");

        string knownHtml = await knownResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        string unknownHtml = await unknownResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        WithoutAntiforgeryTokens(unknownHtml).Should().Be(WithoutAntiforgeryTokens(knownHtml),
                                                          "the body must not betray whether the address has an account");
    }

    /// <summary>Arriving with no address at all - not from a registration flow - just goes home.</summary>
    [Fact]
    public async Task ArrivingWithNoAddressRedirectsHome()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        HttpResponseMessage response = await client.GetAsync("/Account/RegisterConfirmation",
                                                             TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/");
    }
}
