using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// What an administrator's session is shown beyond the administration pages - the menu, the health
/// banner and the detailed health report - and that a closed administrator's cookie stops being shown
/// any of it: the session ends with the account, so the next page is the sign-in page and the health
/// report answers as it answers anybody.
/// </summary>
/// <remarks>
/// <b>Every request arrives from a public address</b>, so an administrator's plain-HTTP session
/// raises the insecure-connection banner. A healthy test host otherwise shows no banner to anybody,
/// and a banner's absence for the closed administrator would prove nothing without one present for
/// the open administrator beside it.
/// </remarks>
public sealed class ClosedAdministratorDisplayTests : IAsyncLifetime
{
    private const string AdminMenu = "id=\"admin\"";
    private const string InsecureBanner = "Insecure connection:";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("closed-admin-display");
    private HomespoolFactory _root = null!;
    private WebApplicationFactory<Controllers.PrinterAppController> _factory = null!;

    public ValueTask InitializeAsync()
    {
        _root = new HomespoolFactory(_scratch);

        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddTransient<IStartupFilter>(_ => new FromAddress(IPAddress.Parse("203.0.113.7")))));

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

    [Fact]
    public async Task AClosedAdministratorsSessionIsShownNothingAnAdministratorIs()
    {
        // Arrange
        (HSUser admin, HttpClient adminClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "admin@example.com", AdminBootstrap.AdminRole);
        (HSUser deputy, HttpClient deputyClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "deputy@example.com", AdminBootstrap.AdminRole);

        using (adminClient)
        using (deputyClient)
        {
            string pageBefore = await GetAsync(deputyClient, "/Printers");
            string healthBefore = await GetAsync(deputyClient, "/health");

            using (IServiceScope scope = _factory.Services.CreateScope())
            {
                UserAdminResult closed = await scope.ServiceProvider.GetRequiredService<UserAdministration>()
                                                    .DeactivateAsync(admin.Id, deputy.Id, TestContext.Current.CancellationToken);
                closed.Succeeded.Should().BeTrue("the fixture has to reach the state being tested");
            }

            // Act
            HttpResponseMessage deputyPage = await deputyClient.GetAsync("/Printers", TestContext.Current.CancellationToken);
            string deputyHealth = await GetAsync(deputyClient, "/health");
            string adminPage = await GetAsync(adminClient, "/Printers");
            string adminHealth = await GetAsync(adminClient, "/health");

            // Assert
            pageBefore.Should().Contain(AdminMenu, "the open deputy is an administrator").And.Contain(InsecureBanner);
            healthBefore.Should().Contain("\"checks\"", "the open deputy is shown the whole report");

            deputyPage.StatusCode.Should().Be(HttpStatusCode.Redirect, "the session ended with the account, role claim and all");
            deputyPage.Headers.Location!.ToString().Should().Contain("/Account/Login");
            deputyHealth.Should().NotContain("\"checks\"", "nor is the detailed report shown, which runs every check on demand")
                        .And.Contain("\"status\"", "the closed session is answered as anybody is");

            adminPage.Should().Contain(AdminMenu, "the administrator who is open keeps the menu").And.Contain(InsecureBanner);
            adminHealth.Should().Contain("\"checks\"");
        }
    }

    private static async Task<string> GetAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{path} is readable by any signed-in account");

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Makes every request arrive from <paramref name="peer"/>. TestServer accepts no connections, so
    /// the peer address is otherwise absent and every session reads as local.
    /// </summary>
    private sealed class FromAddress(IPAddress peer) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = peer;

                    await nextMiddleware();
                });

                next(app);
            };
        }
    }
}
