using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Minting a personal access token takes the account's password, not only a live session.
/// </summary>
/// <remarks>
/// A token is a complete sign-in for everything its scope names, and a password change leaves it
/// standing - so a session somebody else got hold of must not be able to mint one. The page asks
/// for the password through the same step-up gate the passkey and authenticator pages use; what is
/// pinned here is that the gate is actually in front of the create, both ways round: the right
/// password mints, and a wrong or missing one mints nothing and says so.
/// </remarks>
public sealed class ApiTokenStepUpTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("token-step-up");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);
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

    [Fact]
    public async Task TheRightPasswordMintsATokenAndShowsItOnce()
    {
        // Arrange
        (HSUser _, HttpClient client) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "minter@example.com");

        using (client)
        {
            // Act
            using HttpResponseMessage response = await CreateAsync(client, EnrolmentFlowHelper.AccountPassword);
            string page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK, "the secret is rendered by the post itself");
            page.Should().Contain(ApiTokenService.Prefix, "the one-time secret is on the page");

            using IServiceScope scope = _factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<Homespool.Data.HomespoolDbContext>()
                 .ApiTokens.Should().ContainSingle("one token was minted");
        }
    }

    [Fact]
    public async Task TheWrongPasswordMintsNothingAndSaysSo()
    {
        // Arrange
        (HSUser _, HttpClient client) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "guesser@example.com");

        using (client)
        {
            // Act
            using HttpResponseMessage response = await CreateAsync(client, "not-the-current-password");
            string page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK, "the form comes back rather than redirecting");
            page.Should().NotContain(ApiTokenService.Prefix, "no secret is shown");
            page.Should().Contain("That is not your password.", "the refusal says what was wrong");
            page.Should().Contain("value=\"nightly-build\"", "the name typed survives the retry");

            using IServiceScope scope = _factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<Homespool.Data.HomespoolDbContext>()
                 .ApiTokens.Should().BeEmpty("a wrong password mints nothing");
        }
    }

    [Fact]
    public async Task NoPasswordAtAllMintsNothing()
    {
        // Arrange
        (HSUser _, HttpClient client) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "hurried@example.com");

        using (client)
        {
            // Act
            using HttpResponseMessage response = await CreateAsync(client, password: null);
            string page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            page.Should().NotContain(ApiTokenService.Prefix, "no secret is shown");

            using IServiceScope scope = _factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<Homespool.Data.HomespoolDbContext>()
                 .ApiTokens.Should().BeEmpty("the form as a session alone would submit it mints nothing");
        }
    }

    /// <summary>A complete create form - a name and one capability - with whatever password is given.</summary>
    private static async Task<HttpResponseMessage> CreateAsync(HttpClient client, string? password)
    {
        string opened = await client.GetStringAsync("/Account/Manage/ApiTokens", TestContext.Current.CancellationToken);

        List<KeyValuePair<string, string>> fields =
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(opened)),
            new("Input.Name", "nightly-build"),
            new("Input.Scope", Capability.Print.ToString()),
        ];

        if (password is not null)
        {
            fields.Add(new("Input.Password", password));
        }

        using FormUrlEncodedContent form = new(fields);

        return await client.PostAsync("/Account/Manage/ApiTokens", form, TestContext.Current.CancellationToken);
    }
}
