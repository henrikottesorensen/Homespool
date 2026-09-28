using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Certificates;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// A name the certificate covers that resolves to an address this machine answers on is kept by a
/// reissue, without anyone ticking it.
/// </summary>
/// <remarks>
/// Detection cannot discover this machine's other names - from inside a container the hostname is the
/// container's own - so without this the page listed such a name as one to drop, "probably good,
/// unconfirmed", beside the very address it resolves to. The resolver is scripted so the answer is the
/// same on every machine the suite runs on.
/// </remarks>
public sealed class CertificateConfirmedNameTests : IAsyncLifetime
{
    private const string ThisMachine = "this-machine.example.net";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("confirmed");
    private HomespoolFactory _root = null!;
    private WebApplicationFactory<Controllers.PrinterAppController> _factory = null!;

    public ValueTask InitializeAsync()
    {
        // TEST-NET-2, which no machine running the suite has on an interface: only the scripted answer
        // can make the two names meet.
        IPAddress address = IPAddress.Parse("198.51.100.7");

        _root = new HomespoolFactory(_scratch);

        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IHostAddressResolver>(new ScriptedResolver(new Dictionary<string, IPAddress[]>
            {
                [HomespoolFactory.PrinterHost] = [address],
                [ThisMachine] = [address],
            }));
        }));

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _root.DisposeAsync();

        _scratch.Dispose();
    }

    /// <summary>
    /// The confirmed name is not offered for dropping, and the reissue carries it over unticked.
    /// </summary>
    [Fact]
    public async Task ACoveredNameResolvingToThisMachineSurvivesAnUntickedReissue()
    {
        // Arrange - a leaf covering the configured host and another name for the same machine.
        PrinterCertificateAuthority authority = _factory.Services.GetRequiredService<PrinterCertificateAuthority>();
        authority.IssueLeaf([HomespoolFactory.PrinterHost, ThisMachine]).Dispose();

        using HttpClient client = await AdministratorClientAsync();
        string page = await (await client.GetAsync("/Admin/Certificate", TestContext.Current.CancellationToken)).Content
            .ReadAsStringAsync(TestContext.Current.CancellationToken);

        page.Should().NotContainEquivalentOf("would narrow this certificate",
                                             "the name resolves to the address this machine answers on, so nothing is being dropped");

        // Act - no KeepNames at all.
        using FormUrlEncodedContent form = new(
        [
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
        ]);

        using HttpResponseMessage response =
            await client.PostAsync("/Admin/Certificate?handler=Reissue", form, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using X509Certificate2? reissued = authority.LoadLeafIfIssued();

        PrinterCertificateAuthority.NamesOf(reissued!).Should().Contain(ThisMachine,
                                                                        "dropping it would strand every printer provisioned with it");
    }

    private async Task<HttpClient> AdministratorClientAsync()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "admin@example.com", AdminBootstrap.AdminRole);

        await EnrolmentFlowHelper.ReauthenticateAsync(client);

        return client;
    }

    private sealed class ScriptedResolver : IHostAddressResolver
    {
        private readonly Dictionary<string, IPAddress[]> _answers;

        public ScriptedResolver(Dictionary<string, IPAddress[]> answers)
        {
            _answers = answers;
        }

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<IPAddress>>(
                _answers.TryGetValue(name, out IPAddress[]? found) ? found : []);
        }
    }
}
