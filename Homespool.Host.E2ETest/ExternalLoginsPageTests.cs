using System.Collections.Generic;
using System.Net;
using System.Net.Http;
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
/// <c>Account/Manage/ExternalLogins</c> — and mostly the one thing it must never do, which is leave
/// an account with no way to sign in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The invariant is "never zero credentials", not "never a password".</b> An account whose only
/// login is a provider cannot simply drop it: <c>ChangePassword</c> refuses such an account and
/// <c>ForgotPassword</c> is gated for it, so there would be no way back and no administrator-side
/// reset either. The page therefore asks for a password and does both writes in one transaction — a
/// swap, not a removal.
/// </para>
/// <para>
/// <b>The provider is faked at the store rather than driven.</b> These are about what the page does
/// with a linked login, and <c>UserManager.AddLoginAsync</c> puts one there in a line;
/// <c>ExternalOidcDexTests</c> covers the real authorisation-code flow and needs a container to do
/// it. Using a real one here would make every case below depend on dex being up to test something dex
/// has no part in.
/// </para>
/// </remarks>
public sealed class ExternalLoginsPageTests : IAsyncLifetime
{
    /// <summary>Spelled as the sibling suites spell it; see <c>ExternalAccountPasswordTests</c>.</summary>
    private const string Password = "Correct-Horse-Battery-Staple-1!"; // betterleaks:allow

    private const string NewPassword = "A-different-horse-2!"; // betterleaks:allow
    private const string Address = "linked@example.com";
    private const string Provider = "oidc";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("extlogins");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// An account that also has a password removes its provider outright — no password asked for,
    /// because it is not losing its only credential.
    /// </summary>
    [Fact]
    public async Task AnAccountWithAPasswordRemovesAProviderOutright()
    {
        using HttpClient client = await SignedInAsync(withPassword: true, withLogin: true);

        string page = await client.GetStringAsync("/Account/Manage/ExternalLogins", TestContext.Current.CancellationToken);

        page.Should().NotContain("removal-needs-password", "there is a password to fall back on");

        using HttpResponseMessage response = await PostRemoveAsync(client, page, password: null);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        (await LoginCountAsync()).Should().Be(0, "the provider was removed");
        (await HasPasswordAsync()).Should().BeTrue("the password it already had is untouched");
    }

    /// <summary>
    /// The provider-only account is told what removal costs, rather than being offered a button that
    /// would strand it.
    /// </summary>
    [Fact]
    public async Task AProviderOnlyAccountIsAskedForAPasswordBeforeRemoving()
    {
        using HttpClient client = await SignedInAsync(withPassword: false, withLogin: true);

        string page = await client.GetStringAsync("/Account/Manage/ExternalLogins", TestContext.Current.CancellationToken);

        page.Should().Contain("removal-needs-password");
        page.Should().Contain("swap-login-for-password-form");
        page.Should().NotContain($"remove-login-{Provider}",
                                 "a plain remove button here is the lockout this page exists to prevent");
    }

    /// <summary>The swap itself: the password lands and the provider goes, together.</summary>
    [Fact]
    public async Task RemovingTheLastProviderSetsThePasswordAndUnlinksInOneStep()
    {
        using HttpClient client = await SignedInAsync(withPassword: false, withLogin: true);

        string page = await client.GetStringAsync("/Account/Manage/ExternalLogins", TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await PostRemoveAsync(client, page, NewPassword);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "a completed swap redirects back to the page");

        (await HasPasswordAsync()).Should().BeTrue("the account has to keep a way in");
        (await LoginCountAsync()).Should().Be(0, "and the provider it swapped away is gone");
    }

    /// <summary>
    /// A refused password leaves <b>both</b> sides untouched — the assertion the transaction exists
    /// for, and the one that a passing happy path says nothing about.
    /// </summary>
    /// <remarks>
    /// Six characters is the configured minimum, so five fails the length check on the input model
    /// before any write is attempted. The half-done state this rules out is the dangerous one: the
    /// provider removed while the password was rejected leaves the account with nothing at all.
    /// </remarks>
    [Fact]
    public async Task ARefusedPasswordLeavesTheProviderLinkedAndNoPasswordSet()
    {
        using HttpClient client = await SignedInAsync(withPassword: false, withLogin: true);

        string page = await client.GetStringAsync("/Account/Manage/ExternalLogins", TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await PostRemoveAsync(client, page, "short");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the form comes back rather than redirecting");

        (await HasPasswordAsync()).Should().BeFalse("nothing was accepted, so nothing was written");
        (await LoginCountAsync()).Should().Be(1, "and the account keeps the only credential it had");
    }

    private async Task<HttpResponseMessage> PostRemoveAsync(HttpClient client, string page, string? password)
    {
        Dictionary<string, string> form = new()
        {
            ["loginProvider"] = Provider,
            ["providerKey"] = "provider-key",
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page),
        };

        if (password is not null)
        {
            form["Input.NewPassword"] = password;
            form["Input.ConfirmPassword"] = password;
        }

        using FormUrlEncodedContent body = new(form);

        return await client.PostAsync("/Account/Manage/ExternalLogins?handler=RemoveLogin", body,
                                      TestContext.Current.CancellationToken);
    }

    private async Task<HttpClient> SignedInAsync(bool withPassword, bool withLogin)
    {
        await CreateUserAsync(withLogin);

        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        string loginPage = await client.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["Input.Login"] = Address,
            ["Input.Password"] = Password,
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(loginPage),
        });

        using HttpResponseMessage signIn =
            await client.PostAsync("/Account/Login", body, TestContext.Current.CancellationToken);

        signIn.StatusCode.Should().Be(HttpStatusCode.Redirect, "signing in is setup here, not the subject");

        // Removed after signing in, because the sign-in form is the only way to get a cookie and it
        // needs a password. What is under test is the state, not how an account reaches it.
        if (!withPassword)
        {
            using IServiceScope scope = _factory.Services.CreateScope();
            UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

            HSUser user = (await userManager.FindByEmailAsync(Address))!;

            (await userManager.RemovePasswordAsync(user)).Succeeded.Should()
                .BeTrue("the provider-only state is the premise of these tests");
        }

        return client;
    }

    private async Task CreateUserAsync(bool withLogin)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        IUserStore<HSUser> userStore = scope.ServiceProvider.GetRequiredService<IUserStore<HSUser>>();
        IUserEmailStore<HSUser> emailStore = (IUserEmailStore<HSUser>)userStore;
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        HSUser user = new();
        await userStore.SetUserNameAsync(user, EnrolmentFlowHelper.UsernameFor(Address), CancellationToken.None);
        await emailStore.SetEmailAsync(user, Address, CancellationToken.None);
        user.EmailConfirmed = true;

        (await userManager.CreateAsync(user, Password)).Succeeded.Should()
            .BeTrue("account creation is setup for these tests, not what they verify");

        if (withLogin)
        {
            (await userManager.AddLoginAsync(user, new UserLoginInfo(Provider, "provider-key", Provider)))
                .Succeeded.Should().BeTrue();
        }
    }

    private async Task<bool> HasPasswordAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        return await userManager.HasPasswordAsync((await userManager.FindByEmailAsync(Address))!);
    }

    private async Task<int> LoginCountAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        return (await userManager.GetLoginsAsync((await userManager.FindByEmailAsync(Address))!)).Count;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }
}
