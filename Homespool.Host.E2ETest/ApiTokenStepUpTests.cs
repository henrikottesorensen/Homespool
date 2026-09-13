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
/// Minting a personal access token takes a recent proof, not only a live session.
/// </summary>
/// <remarks>
/// A token is a complete sign-in for everything its scope names, and a password change leaves it
/// standing - so a session somebody else got hold of must not be able to mint one. The create handler
/// is behind the same proof every gated page uses; what is pinned here is that the gate is actually in
/// front of the create, both ways round: a proved session mints, and an unproved one is sent to prove
/// and mints nothing.
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
    public async Task AProvedSessionMintsATokenAndShowsItOnce()
    {
        // Arrange
        (HSUser _, HttpClient client) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "minter@example.com");

        using (client)
        {
            await EnrolmentFlowHelper.ReauthenticateAsync(client);

            // Act
            using HttpResponseMessage response = await CreateAsync(client);
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
    public async Task AnUnprovedSessionIsSentToProveAndMintsNothing()
    {
        // Arrange
        (HSUser _, HttpClient client) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "hurried@example.com");

        using (client)
        {
            // Act
            using HttpResponseMessage response = await CreateAsync(client);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Redirect, "the form as a session alone would submit it is sent to prove first");
            response.Headers.Location!.OriginalString.Should().Contain("/Account/Reauthenticate");

            using IServiceScope scope = _factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<Homespool.Data.HomespoolDbContext>()
                 .ApiTokens.Should().BeEmpty("nothing is minted without a proof");
        }
    }

    /// <summary>The page offers the create button only to a proved session, and the way to prove otherwise.</summary>
    [Fact]
    public async Task TheCreateButtonWaitsForAProof()
    {
        (HSUser _, HttpClient client) =
            await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "reader@example.com");

        using (client)
        {
            string before = await client.GetStringAsync("/Account/Manage/ApiTokens", TestContext.Current.CancellationToken);
            await EnrolmentFlowHelper.ReauthenticateAsync(client);
            string after = await client.GetStringAsync("/Account/Manage/ApiTokens", TestContext.Current.CancellationToken);

            before.Should().Contain("/Account/Reauthenticate", "the unproved page links to the proof");
            after.Should().NotContain("/Account/Reauthenticate", "the proved page offers the act itself");
        }
    }

    /// <summary>A complete create form - a name and one capability.</summary>
    private static async Task<HttpResponseMessage> CreateAsync(HttpClient client)
    {
        string opened = await client.GetStringAsync("/Account/Manage/ApiTokens", TestContext.Current.CancellationToken);

        List<KeyValuePair<string, string>> fields =
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(opened)),
            new("Input.Name", "nightly-build"),
            new("Input.Scope", Capability.Print.ToString()),
        ];

        using FormUrlEncodedContent form = new(fields);

        return await client.PostAsync("/Account/Manage/ApiTokens", form, TestContext.Current.CancellationToken);
    }
}
