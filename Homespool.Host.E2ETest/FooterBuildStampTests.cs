using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The build stamp in the footer, and the one thing about it that is a decision rather than a layout
/// choice: it is shown to signed-in users only.
/// </summary>
/// <remarks>
/// <para>
/// <b>The anonymous case is the test that matters.</b> The shared layout renders on the sign-in and
/// registration pages, so an unguarded stamp would put the exact commit of the running deployment on
/// a public page - which names precisely which known defects apply, under the standing assumption
/// that the deployment is internet-facing. The guard is one
/// <c>@@if</c> in a shared file, which is exactly the kind of thing a later edit removes without
/// noticing, and nothing else in the suite would go red.
/// </para>
/// <para>
/// The unit tests beside this one cover what the stamp <i>says</i>; this covers only who can see it,
/// which is the half that cannot be checked without rendering a page as two different callers.
/// </para>
/// </remarks>
public sealed class FooterBuildStampTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-footer-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ASignedInPageCarriesTheBuildStampAndAnAnonymousOneDoesNot()
    {
        // Guards the two assertions below against passing vacuously. Both reduce to "the page does
        // not contain an empty string" if this process carries no stamp, which would look like a
        // clean pass while checking nothing at all.
        string stamp = BuildInformation.Summary;
        stamp.Should().NotBeNullOrWhiteSpace(
            "the assertions below cannot tell the two cases apart without a stamp to look for");

        using HttpClient anonymous = _factory.CreateClient();
        string anonymousBody = await anonymous.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);

        (HSUser _, HttpClient signedIn) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "footer@example.com");

        using (signedIn)
        {
            string signedInBody = await signedIn.GetStringAsync("/", TestContext.Current.CancellationToken);

            signedInBody.Should().Contain(stamp);
            anonymousBody.Should().NotContain(stamp);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _factory?.Dispose();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
