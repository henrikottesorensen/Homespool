using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// An invite never touches an account that already exists: whatever the address holds, redeeming is
/// refused and nothing about that account changes.
/// </summary>
/// <remarks>
/// <para>
/// <b>It used to adopt one.</b> An address holding a password-less account had that account taken
/// over by the invite - a password set, every provider login removed - which existed because an
/// account orphaned by a dead identity provider had no other way back. Two audits found what that
/// cost: an ordinary invite could silently re-credential a <em>working</em> provider account without
/// telling its owner, and it ignored <c>DeactivatedAt</c>, so a closed account could come back under
/// the invite-holder's password the moment an administrator reopened it.
/// </para>
/// <para>
/// <b>Re-credentialing an existing account is now the recovery invite's job alone</b>, which an
/// administrator issues by naming the account, which refuses a deactivated one, and which tells the
/// owner it happened. So this file's subject is the rule that replaced adoption: an invite creates an
/// account or is refused, and there is no third thing it can do.
/// </para>
/// <para>
/// <b>The last test is what keeps the first three honest.</b> A refusal that refused everything would
/// satisfy every negative assertion here; an ordinary invite for an address nobody holds still has to
/// create an account.
/// </para>
/// </remarks>
public sealed class InviteToAnExistingAddressTests : IAsyncLifetime
{
    private const string Password = "Correct-Horse-Battery-Staple-1!"; // betterleaks:allow
    private const string Address = "orphan@example.com";
    private const string Provider = "oidc";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("reactivate");
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
    /// The case adoption was built for, and the one that changed: an orphaned account is refused like
    /// any other, and keeps having no password.
    /// </summary>
    [Fact]
    public async Task AnInviteForAnOrphanedAccountIsRefusedAndChangesNothing()
    {
        long orphanId = await CreateOrphanedAccountAsync();
        (int inviteId, string code) = await CreateInviteAsync(Address);

        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using HttpResponseMessage response = await AcceptAsync(client, inviteId, code, username: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the form comes back with the refusal on it");

        string page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        page.Should().Contain("send them a recovery link",
                              "the refusal points at the route that does re-credential an account");

        (await CountAccountsForAddressAsync()).Should().Be(1, "and no second account was made either");
        (await FindAsync()).Id.Should().Be(orphanId);
        (await HasPasswordAsync()).Should().BeFalse("an invite may not hand this account a credential");
        (await LoginCountAsync()).Should().Be(1, "nor take its provider link away");
    }

    /// <summary>
    /// An invite aimed at an address whose account still works is refused — and, more importantly,
    /// changes nothing about it.
    /// </summary>
    /// <remarks>
    /// Before the reactivation branch this was a duplicate-address validation error. It is still
    /// refused without the explicit check — <c>AddPasswordAsync</c> sees to that — so what is under
    /// test here is <em>which</em> refusal arrives, and the account being untouched underneath it.
    /// </remarks>
    [Fact]
    public async Task AnInviteForAnAddressThatAlreadySignsInIsRefused()
    {
        await CreateWorkingAccountAsync();
        (int inviteId, string code) = await CreateInviteAsync(Address);

        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using HttpResponseMessage response = await AcceptAsync(client, inviteId, code, username: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the form comes back with the refusal on it");

        string page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        page.Should().Contain("already has an account",
                              "the refusal names what is actually wrong; Identity's fallback talks about a "
                              + "password the caller was never asked for and does not know they have");

        (await CountAccountsForAddressAsync()).Should().Be(1);

        // The password it already had, unchanged - the assertion that separates "refused" from
        // "quietly re-credentialled".
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        (await users.CheckPasswordAsync((await users.FindByEmailAsync(Address))!, Password)).Should()
            .BeTrue("an invite must not be able to replace a working credential");
    }

    /// <summary>
    /// An invite for an address nobody holds still creates an account, exactly as before.
    /// </summary>
    /// <remarks>
    /// Without this the branch above could refuse everything and both tests would still pass — the
    /// shape a negative assertion cannot see on its own.
    /// </remarks>
    [Fact]
    public async Task AnInviteForANewAddressStillCreatesAnAccount()
    {
        (int inviteId, string code) = await CreateInviteAsync("newcomer@example.com");

        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using HttpResponseMessage response = await AcceptAsync(client, inviteId, code, username: "newcomer");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        (await users.FindByEmailAsync("newcomer@example.com")).Should().NotBeNull("ordinary invites are unaffected");
    }

    private async Task<HttpResponseMessage> AcceptAsync(HttpClient client, int inviteId, string code, string? username)
    {
        string page = await client.GetStringAsync($"/Account/Register?InviteId={inviteId}&Code={code}",
                                                  TestContext.Current.CancellationToken);

        Dictionary<string, string> form = new()
        {
            ["InviteId"] = inviteId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Code"] = code,
            ["Input.Password"] = Password,
            ["Input.ConfirmPassword"] = Password,
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page),
        };

        if (username is not null)
        {
            form["Input.Username"] = username;
        }

        using FormUrlEncodedContent body = new(form);

        return await client.PostAsync($"/Account/Register?InviteId={inviteId}&Code={code}", body,
                                      TestContext.Current.CancellationToken);
    }

    /// <summary>An account as <c>ExternalLogin</c> creates one: no password, one provider link.</summary>
    private async Task<long> CreateOrphanedAccountAsync(bool confirmed = true, bool withAuthenticator = false)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        IUserStore<HSUser> store = scope.ServiceProvider.GetRequiredService<IUserStore<HSUser>>();
        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        HSUser user = new();
        await store.SetUserNameAsync(user, "orphan", CancellationToken.None);
        await ((IUserEmailStore<HSUser>)store).SetEmailAsync(user, Address, CancellationToken.None);
        user.EmailConfirmed = confirmed;

        (await users.CreateAsync(user)).Succeeded.Should().BeTrue();
        (await users.AddLoginAsync(user, new UserLoginInfo(Provider, "dead-subject", Provider))).Succeeded.Should().BeTrue();

        if (withAuthenticator)
        {
            (await users.ResetAuthenticatorKeyAsync(user)).Succeeded.Should().BeTrue();
            (await users.SetTwoFactorEnabledAsync(user, true)).Succeeded.Should().BeTrue();
        }

        return user.Id;
    }

    private async Task CreateWorkingAccountAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        IUserStore<HSUser> store = scope.ServiceProvider.GetRequiredService<IUserStore<HSUser>>();
        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        HSUser user = new();
        await store.SetUserNameAsync(user, "working", CancellationToken.None);
        await ((IUserEmailStore<HSUser>)store).SetEmailAsync(user, Address, CancellationToken.None);
        user.EmailConfirmed = true;

        (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();
    }

    private async Task<(int inviteId, string code)> CreateInviteAsync(string email)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        (Invitation invitation, string token) = await scope.ServiceProvider.GetRequiredService<InvitationService>()
            .CreateAsync(email, teamId: null, invitedBy: 1, expiresAt: null, TestContext.Current.CancellationToken);

        return (invitation.Id, WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token)));
    }

    private async Task<HSUser> FindAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        return (await scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>().FindByEmailAsync(Address))!;
    }

    private async Task<bool> HasPasswordAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        return await users.HasPasswordAsync((await users.FindByEmailAsync(Address))!);
    }

    private async Task<int> LoginCountAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        return (await users.GetLoginsAsync((await users.FindByEmailAsync(Address))!)).Count;
    }

    private async Task<int> CountAccountsForAddressAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        Data.HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<Data.HomespoolDbContext>();

        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(context.Users),
            u => u.Email == Address,
            TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }
}
