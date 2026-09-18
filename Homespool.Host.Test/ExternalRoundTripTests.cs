using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;

using AwesomeAssertions;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Authentication;
using Homespool.Host.Pages.Account.Manage;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// An external provider's answer is read only by the flow that asked for it.
/// </summary>
/// <remarks>
/// <b>The first test is the walk-around this exists for</b>, driven through the real link callback: a
/// session on a provider-only account starts a re-authentication, which needs no proof, signs in at the
/// provider as somebody else, and opens the link callback with that answer. Before the flow was named
/// the callback checked only whose account the answer was for, which a re-authentication's answer
/// satisfies, and linked the foreign identity. The rest pin the refusal in every direction and that each
/// flow still reads its own answer - a reader that refused everything would pass the refusals.
/// </remarks>
public sealed class ExternalRoundTripTests : IDisposable
{
    private const string Provider = "oidc";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-roundtrip-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task TheLinkCallbackWillNotLinkAReauthenticationsAnswer()
    {
        // Arrange - a provider-only account, and the answer to its re-authentication naming a stranger
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser victim = await ProviderOnlyAccountAsync(rig, "victim@example.com", "victim-subject");
        string answer = await AnswerAsync(rig, "attacker-subject", ExternalRoundTrip.Reauthenticate, victim);

        ExternalLoginsModel page = await LinkCallbackPageAsync(rig, victim, answer);

        // Act
        await page.OnGetLinkLoginCallbackAsync();

        // Assert
        (await rig.Users.FindByLoginAsync(Provider, "attacker-subject")).Should().BeNull("a re-authentication's answer is not a link");
        (await rig.Users.GetLoginsAsync(victim)).Should().ContainSingle("the account keeps only the login it had");
    }

    /// <summary>The counterweight: the link flow's own answer still links, or the refusal above proves nothing.</summary>
    [Fact]
    public async Task TheLinkCallbackLinksItsOwnAnswer()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser owner = await ProviderOnlyAccountAsync(rig, "owner@example.com", "first-subject");
        string answer = await AnswerAsync(rig, "second-subject", ExternalRoundTrip.Link, owner);

        ExternalLoginsModel page = await LinkCallbackPageAsync(rig, owner, answer);

        await page.OnGetLinkLoginCallbackAsync();

        (await rig.Users.FindByLoginAsync(Provider, "second-subject"))!.Id.Should().Be(owner.Id);
    }

    [Theory]
    [InlineData(ExternalRoundTrip.Reauthenticate, ExternalRoundTrip.Link)]
    [InlineData(ExternalRoundTrip.Link, ExternalRoundTrip.Reauthenticate)]
    [InlineData(ExternalRoundTrip.Link, ExternalRoundTrip.SignIn)]
    [InlineData(ExternalRoundTrip.Reauthenticate, ExternalRoundTrip.SignIn)]
    public async Task AnAnswerIsRefusedByAFlowThatDidNotAskForIt(ExternalRoundTrip askedBy, ExternalRoundTrip readBy)
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string answer = await AnswerAsync(rig, "some-subject", askedBy, user);

        ExternalLoginInfo? info = await ReadAsync(rig, answer, readBy, readBy is ExternalRoundTrip.SignIn ? null : user);

        info.Should().BeNull();
    }

    /// <summary>A sign-in's answer names no account, so a signed-in flow cannot read it as its own either.</summary>
    [Theory]
    [InlineData(ExternalRoundTrip.Link)]
    [InlineData(ExternalRoundTrip.Reauthenticate)]
    public async Task ASignInsAnswerIsRefusedBySignedInFlows(ExternalRoundTrip readBy)
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string answer = await AnswerAsync(rig, "some-subject", ExternalRoundTrip.SignIn, expectedAccount: null);

        (await ReadAsync(rig, answer, readBy, user)).Should().BeNull();
    }

    [Theory]
    [InlineData(ExternalRoundTrip.SignIn)]
    [InlineData(ExternalRoundTrip.Link)]
    [InlineData(ExternalRoundTrip.Reauthenticate)]
    public async Task EachFlowReadsItsOwnAnswer(ExternalRoundTrip roundTrip)
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        HSUser? expected = roundTrip is ExternalRoundTrip.SignIn ? null : user;
        string answer = await AnswerAsync(rig, "some-subject", roundTrip, expected);

        ExternalLoginInfo? info = await ReadAsync(rig, answer, roundTrip, expected);

        info.Should().NotBeNull();
        info!.ProviderKey.Should().Be("some-subject");
    }

    /// <summary>An answer that names no flow - a round trip started before flows were named - is nobody's.</summary>
    [Theory]
    [InlineData(ExternalRoundTrip.SignIn)]
    [InlineData(ExternalRoundTrip.Link)]
    [InlineData(ExternalRoundTrip.Reauthenticate)]
    public async Task AnAnswerNamingNoFlowIsRefusedByEveryReader(ExternalRoundTrip readBy)
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        HSUser? expected = readBy is ExternalRoundTrip.SignIn ? null : user;

        AuthenticationProperties legacy = new();
        legacy.Items[ExternalSignIn.LoginProviderItem] = Provider;

        if (expected is not null)
        {
            legacy.Items[ExternalSignIn.ExpectedAccountItem] = expected.Id.ToString();
        }

        string answer = await AnswerAsync(rig, "some-subject", legacy);

        (await ReadAsync(rig, answer, readBy, expected)).Should().BeNull();
    }

    /// <summary>
    /// The default flow is not a flow anybody started: a round trip nobody named is refused at both
    /// ends, loudly, rather than being read as whichever member happens to sit at zero.
    /// </summary>
    [Fact]
    public async Task TheDefaultRoundTripIsNotAFlowAnybodyStarted()
    {
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync("owner@example.com");
        string answer = await AnswerAsync(rig, "some-subject", ExternalRoundTrip.SignIn, expectedAccount: null);

        Action start = () => ExternalSignIn.ChallengeProperties(Provider, "/back", default, user.Id.ToString());
        Func<Task> read = () => ReadAsync(rig, answer, default, expectedAccount: null);

        start.Should().Throw<ArgumentOutOfRangeException>();
        await read.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(ExternalRoundTrip.Link)]
    [InlineData(ExternalRoundTrip.Reauthenticate)]
    public void ASignedInFlowMustNameItsAccount(ExternalRoundTrip roundTrip)
    {
        Action challenge = () => ExternalSignIn.ChallengeProperties(Provider, "/back", roundTrip, expectedAccountId: null);

        challenge.Should().Throw<ArgumentException>();
    }

    private static async Task<HSUser> ProviderOnlyAccountAsync(LocalSchemeRig rig, string email, string subject)
    {
        HSUser user = await rig.AddUserAsync(email);
        (await rig.Users.RemovePasswordAsync(user)).Succeeded.Should().BeTrue();
        (await rig.Users.AddLoginAsync(user, new UserLoginInfo(Provider, subject, "Dex"))).Succeeded.Should().BeTrue();

        return user;
    }

    /// <summary>The external cookie a provider's answer to <paramref name="roundTrip"/> leaves, as the browser sends it back.</summary>
    private static Task<string> AnswerAsync(LocalSchemeRig rig, string subject, ExternalRoundTrip roundTrip, HSUser? expectedAccount)
    {
        return AnswerAsync(rig, subject, ExternalSignIn.ChallengeProperties(Provider, "/back", roundTrip, expectedAccount?.Id.ToString()));
    }

    /// <summary>
    /// The external cookie carrying <paramref name="properties"/>, as the provider handler writes it on
    /// the way back: the challenge's items, protected with the principal.
    /// </summary>
    private static async Task<string> AnswerAsync(LocalSchemeRig rig, string subject, AuthenticationProperties properties)
    {
        DefaultHttpContext request = rig.NewRequest();
        ClaimsPrincipal principal = new(new ClaimsIdentity([new Claim(JwtClaimTypes.Subject, subject)], Provider));

        await request.SignInAsync(IdentityConstants.ExternalScheme, principal, properties);

        return rig.CookieOf(request, IdentityConstants.ExternalScheme);
    }

    private static async Task<ExternalLoginInfo?> ReadAsync(LocalSchemeRig rig, string answer, ExternalRoundTrip readBy, HSUser? expectedAccount)
    {
        DefaultHttpContext request = rig.NewRequest(answer);

        return await request.RequestServices.GetRequiredService<ExternalSignIn>()
                            .InfoAsync(request, readBy, expectedAccount?.Id.ToString());
    }

    /// <summary>The link page over a request signed in as <paramref name="user"/> and carrying <paramref name="answer"/>.</summary>
    private static async Task<ExternalLoginsModel> LinkCallbackPageAsync(LocalSchemeRig rig, HSUser user, string answer)
    {
        DefaultHttpContext request = rig.NewRequest(await rig.SessionCookieAsync(user), answer);
        request.Response.Body = new MemoryStream();
        request.User = (await request.AuthenticateAsync(IdentityConstants.ApplicationScheme)).Principal!;

        IServiceProvider services = request.RequestServices;

        return new ExternalLoginsModel(rig.Users,
                                       services.GetRequiredService<LocalSignIn>(),
                                       services.GetRequiredService<ExternalSignIn>(),
                                       services.GetRequiredService<RecentProof>(),
                                       new UnitOfWork(services.GetRequiredService<HomespoolDbContext>()),
                                       new CapturingEmailSender().Notices(),
                                       NullLogger<ExternalLoginsModel>.Instance,
                                       TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(request),
            Url = IdentityTestHarness.NewUrlHelper(request),
        };
    }
}
