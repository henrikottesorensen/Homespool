using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Services;

namespace Homespool.Host.E2ETest;

/// <summary>
/// That the external-provider handler refuses a name nobody registered, rather than faulting on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The scaffold is deliberately kept</b> — external identity providers are scoped out rather than
/// rejected (<c>notes/phase-1.5-enrollment.md</c>, "External OIDC scoped out"), and
/// <c>notes/user-identity.md</c> records it being maintained rather than deleted for exactly that
/// reason. So the fix here is a check, not a removal.
/// </para>
/// <para>
/// Nothing in the UI reaches this: with no provider registered the login page renders no buttons,
/// because <c>GetExternalAuthenticationSchemesAsync</c> is empty. The caller is a hand-made request,
/// and what it used to get was a 500 from <c>ChallengeResult</c> on an unregistered scheme.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class ExternalLoginProviderTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-extlogin-{Guid.NewGuid():N}.db");
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
    /// Three shapes that all used to reach <c>ChallengeResult</c>: a name nobody has heard of, an
    /// empty one, and — the interesting one — <c>PrusaConnect</c>, which <i>is</i> a registered
    /// authentication scheme but is the printer protocol's, not an external identity provider's.
    /// Challenging it from a sign-in page got its 401; harmless, and not a thing to leave reachable.
    /// </summary>
    [Theory]
    [InlineData("NoSuchProvider")]
    [InlineData("")]
    [InlineData("PrusaConnect")]
    [InlineData("ApiToken")]
    public async Task AProviderNobodyRegisteredIsRefused(string provider)
    {
        using HttpClient client = _factory.CreateClient();

        string page = await client.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["provider"] = provider,
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page),
        });

        using HttpResponseMessage response =
            await client.PostAsync("/Account/ExternalLogin", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                                        "an unregistered scheme reaching ChallengeResult throws, so this answered 500 "
                                        + "to any anonymous caller who asked");
    }

    /// <summary>
    /// And the reason the page is still here at all: with no provider configured, the sign-in page
    /// offers none rather than showing a button that cannot work.
    /// </summary>
    [Fact]
    public async Task TheSignInPageOffersNoProviderWhileNoneIsRegistered()
    {
        using HttpClient client = _factory.CreateClient();

        string page = await client.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);

        page.Should().NotContain("/Account/ExternalLogin",
                                 "GetExternalAuthenticationSchemesAsync is empty, so there is nothing to offer");
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _factory.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
