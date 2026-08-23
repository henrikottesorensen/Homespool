using System;
using System.Collections.Generic;
using System.IO;
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

using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Recovering an account orphaned by its identity provider: an administrator sends it an invite, and
/// redeeming that invite gives the <em>existing</em> account a password instead of making a new one.
/// </summary>
/// <remarks>
/// <para>
/// <b>This did not work at all before, and the failure was silent in the worst way.</b>
/// <c>RequireUniqueEmail</c> is on and invite-accept created unconditionally, so an invite sent to an
/// orphaned account's address failed on a duplicate address — an invite that cannot be redeemed, and
/// the only recovery route the deployment had. There is no administrator-side password reset behind
/// it.
/// </para>
/// <para>
/// <b>Only accounts with no password are adoptable</b>, which is exactly the orphaned set. That
/// check is <b>not</b> the only thing standing between an invite and an account takeover, and an
/// earlier version of this remark claimed it was: <c>AddPasswordAsync</c> refuses an account that
/// already has one, so removing the check leaves the outcome unchanged. Measured, by deleting it —
/// all three tests still passed, which is how the overclaim was caught and how the test below was
/// found to be asserting nothing.
/// </para>
/// <para>
/// <b>So what the check buys is a legible refusal, and that is what the test now asserts.</b> Without
/// it the caller meets Identity's "user already has a password" on a form that never asked about
/// passwords they hold. The two refusals are indistinguishable in stored state — same status, same
/// unchanged account, same unspent invite — so the assertion has to read the message, and is coupled
/// to its wording on purpose for want of anything else that separates them.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class OrphanedAccountReactivationTests : IAsyncLifetime, IDisposable
{
    private const string Password = "Correct-Horse-Battery-Staple-1!"; // betterleaks:allow
    private const string Address = "orphan@example.com";
    private const string Provider = "oidc";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-reactivate-{Guid.NewGuid():N}.db");
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
    /// The whole point: one account before, one after, now with a password and without the dead link.
    /// </summary>
    [Fact]
    public async Task RedeemingAnInviteReactivatesTheOrphanedAccountRatherThanCreatingASecond()
    {
        long orphanId = await CreateOrphanedAccountAsync();
        (int inviteId, string code) = await CreateInviteAsync(Address);

        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using HttpResponseMessage response = await AcceptAsync(client, inviteId, code, username: null);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "a reactivated account is signed straight in");

        (await CountAccountsForAddressAsync()).Should()
            .Be(1, "reactivation adopts the account that exists; a second one is the bug this prevents");

        HSUser user = await FindAsync();

        user.Id.Should().Be(orphanId, "and it is the same account, not a replacement wearing its address");
        (await HasPasswordAsync()).Should().BeTrue("which is the whole point of sending the invite");
        (await LoginCountAsync()).Should().Be(0, "the dead provider link goes in the same step");
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

        page.Should().Contain("already has an account that can sign in",
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
    private async Task<long> CreateOrphanedAccountAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        IUserStore<HSUser> store = scope.ServiceProvider.GetRequiredService<IUserStore<HSUser>>();
        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        HSUser user = new();
        await store.SetUserNameAsync(user, "orphan", CancellationToken.None);
        await ((IUserEmailStore<HSUser>)store).SetEmailAsync(user, Address, CancellationToken.None);
        user.EmailConfirmed = true;

        (await users.CreateAsync(user)).Succeeded.Should().BeTrue();
        (await users.AddLoginAsync(user, new UserLoginInfo(Provider, "dead-subject", Provider))).Succeeded.Should().BeTrue();

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
