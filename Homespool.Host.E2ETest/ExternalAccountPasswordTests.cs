using System;
using System.Collections.Generic;
using System.IO;
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
/// An account whose credential is an external provider has no local password, and cannot acquire
/// one — through its own account menu or through the reset flow.
/// </summary>
/// <remarks>
/// <para>
/// <b>The state these cover was a 500 until 2026-08-22.</b> <c>ChangePasswordModel.OnGetAsync</c>
/// redirected to <c>./SetPassword</c>, an Identity.UI page that never came across when the package
/// was removed, and an unresolvable <c>RedirectToPage</c> throws in the executor rather than
/// answering. Nothing reached it before external OIDC existed, because every other creation path sets
/// a password — which is exactly why it needs a test rather than a fix and a shrug.
/// </para>
/// <para>
/// <b>The page was not restored, and that is the decision being pinned here</b> (Henrik, 2026-08-22):
/// an external account does not get a local password at all. So there are two halves to hold, and
/// the second is the one that would rot quietly — <c>ForgotPassword</c> would otherwise hand out a
/// reset token to an account that has no password, and <c>ResetPasswordAsync</c> writes the hash
/// whether or not one exists. A rule enforced on the page a person can see and not on the one they
/// can ask for by email is not a rule.
/// </para>
/// <para>
/// <b>The fixture signs in with a password and then removes it</b>, rather than driving a provider.
/// What is under test is the passwordless state, not how an account arrives in it; the OIDC path that
/// produces it for real is covered by <c>ExternalOidcDexTests</c>, which needs a container. Removing
/// the password moves the security stamp, which is survivable here only because
/// <c>SecurityStampValidator</c> revalidates on an interval rather than per request.
/// </para>
/// </remarks>
public sealed class ExternalAccountPasswordTests : IAsyncLifetime, IDisposable
{
    /// <summary>
    /// The fixture password, spelled the same way <see cref="LoginFlowTests"/> and
    /// <see cref="LoginWith2faTests"/> spell theirs — one obvious placeholder across the suite rather
    /// than a new plausible-looking one per class.
    /// </summary>
    /// <remarks>
    /// The allow-comment is not this string being special: <c>generic-password</c> fires on any
    /// literal assigned to something called <c>Password</c>, and the two sibling classes carry the
    /// identical value only because they predate the hook, which scans staged diffs rather than the
    /// tree. Every new fixture password will meet this.
    /// </remarks>
    private const string Password = "Correct-Horse-Battery-Staple-1!"; // betterleaks:allow
    private const string Address = "provider-user@example.com";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-extpwd-{Guid.NewGuid():N}.db");
    private readonly CapturingSink _logs = new();
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}", extraSinks: [_logs]);

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The account menu's <i>Password</i> entry, followed by an account that has none. It answers,
    /// and says why, instead of throwing.
    /// </summary>
    [Fact]
    public async Task AnAccountWithNoPasswordIsToldSoRatherThanFaulting()
    {
        using HttpClient client = await SignedInWithoutAPasswordAsync();

        using HttpResponseMessage response =
            await client.GetAsync("/Account/Manage/ChangePassword", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
                                        "this redirected to a page that does not exist, which throws rather than 404s");

        string page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        page.Should().Contain("no-local-password", "the account is told why there is nothing to change");
        page.Should().NotContain("change-password-form",
                                 "offering the form would invite a change that cannot succeed");
    }

    /// <summary>
    /// Withholding the form is not refusing the post. A hand-made request is refused too, and — the
    /// assertion that matters — sets no password.
    /// </summary>
    [Fact]
    public async Task PostingTheChangeFormAnywayDoesNotGiveTheAccountAPassword()
    {
        using HttpClient client = await SignedInWithoutAPasswordAsync();

        string page = await client.GetStringAsync("/Account/Manage/ChangePassword", TestContext.Current.CancellationToken);

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["Input.OldPassword"] = Password,
            ["Input.NewPassword"] = Password + "-changed",
            ["Input.ConfirmPassword"] = Password + "-changed",
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page),
        });

        using HttpResponseMessage response =
            await client.PostAsync("/Account/Manage/ChangePassword", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the post is answered, not redirected as a success");

        (await HasPasswordAsync()).Should()
            .BeFalse("an external account acquiring a password is the thing this refuses");
    }

    /// <summary>
    /// The reset door, which is the half that would otherwise make the rule decorative:
    /// <c>ResetPasswordAsync</c> writes a hash whether or not one exists, so an unguarded
    /// <c>ForgotPassword</c> is a way for an external account to give itself a password by mail.
    /// </summary>
    /// <remarks>
    /// The refusal is <b>silent</b> and the assertion says so: this arm already existed to avoid
    /// revealing whether an address is registered, and a refusal that looked different here would
    /// answer that question for anybody who cared to ask.
    /// </remarks>
    [Fact]
    public async Task AskingForAResetOnAnExternalAccountLooksExactlyLikeAskingForAnUnknownAddress()
    {
        await CreateUserAsync(Address);
        await RemoveThePasswordAsync();

        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage external = await PostForgotPasswordAsync(client, Address);
        HttpResponseMessage unknown = await PostForgotPasswordAsync(client, "nobody-at-all@example.com");

        external.StatusCode.Should().Be(unknown.StatusCode);
        external.Headers.Location!.OriginalString.Should()
                .Be(unknown.Headers.Location!.OriginalString,
                    "a distinguishable refusal would reveal which accounts sign in with a provider");
        external.Headers.Location!.OriginalString.Should().Contain("ForgotPasswordConfirmation");

        // The response cannot carry this assertion, and that is the point: an unguarded
        // ForgotPassword answers with the identical redirect after sending the mail, so asserting on
        // the response alone passes just as well against no guard at all. What separates the two is
        // whether a reset link went out, which only the send attempt records - SMTP is unconfigured
        // here, so LoggingEmailSender logs every attempt with the address it would have used.
        _logs.HasEventWith(("Email", Address)).Should()
             .BeFalse("an external account must not be sent a reset link it could use to acquire a password");

        external.Dispose();
        unknown.Dispose();
    }

    /// <summary>
    /// The guard did not close the door on everybody: an ordinary account still gets its reset.
    /// </summary>
    /// <remarks>
    /// Without this the test above passes just as well against a <c>ForgotPassword</c> that refuses
    /// unconditionally, which is the failure mode a non-disclosure assertion cannot see.
    /// </remarks>
    [Fact]
    public async Task AnAccountThatHasAPasswordCanStillAskForAReset()
    {
        await CreateUserAsync("ordinary@example.com");

        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using HttpResponseMessage response = await PostForgotPasswordAsync(client, "ordinary@example.com");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("ForgotPasswordConfirmation");

        _logs.HasEventWith(("Email", "ordinary@example.com")).Should()
             .BeTrue("the guard is about accounts with no password, not about reset in general");
    }

    private async Task<HttpResponseMessage> PostForgotPasswordAsync(HttpClient client, string email)
    {
        string page = await client.GetStringAsync("/Account/ForgotPassword", TestContext.Current.CancellationToken);

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page),
        });

        return await client.PostAsync("/Account/ForgotPassword", body, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A client holding the application cookie for an account that now has no password — signed in
    /// while it still had one, because the sign-in form is the only way in and it needs a password.
    /// </summary>
    private async Task<HttpClient> SignedInWithoutAPasswordAsync()
    {
        await CreateUserAsync(Address);

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

        await RemoveThePasswordAsync();

        return client;
    }

    private async Task CreateUserAsync(string email)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        IUserStore<HSUser> userStore = scope.ServiceProvider.GetRequiredService<IUserStore<HSUser>>();
        IUserEmailStore<HSUser> emailStore = (IUserEmailStore<HSUser>)userStore;
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        HSUser user = new();
        await userStore.SetUserNameAsync(user, EnrolmentFlowHelper.UsernameFor(email), CancellationToken.None);
        await emailStore.SetEmailAsync(user, email, CancellationToken.None);
        user.EmailConfirmed = true;

        IdentityResult result = await userManager.CreateAsync(user, Password);
        result.Succeeded.Should().BeTrue("account creation is setup for these tests, not what they verify");
    }

    /// <summary>
    /// Leaves the account in the state an external sign-in creates: a user row with no password hash.
    /// </summary>
    private async Task RemoveThePasswordAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        HSUser user = (await userManager.FindByEmailAsync(Address))!;
        IdentityResult result = await userManager.RemovePasswordAsync(user);

        result.Succeeded.Should().BeTrue("the passwordless state is the premise of these tests");
    }

    private async Task<bool> HasPasswordAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        return await userManager.HasPasswordAsync((await userManager.FindByEmailAsync(Address))!);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _factory.Dispose();

        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch (IOException)
        {
        }
    }
}
