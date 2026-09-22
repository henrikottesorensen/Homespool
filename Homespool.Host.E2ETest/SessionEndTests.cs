using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// A session ends for good: once a browser has signed out, the cookie it held - copied off the
/// machine beforehand, say - signs nobody in, over HTTP as well as in the scheme's own tests.
/// </summary>
public sealed class SessionEndTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("session-end");

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
    public async Task ACopyOfTheCookieIsRefusedOnceTheBrowserHasSignedOut()
    {
        // Arrange
        (HSUser _, HttpClient browser) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "owner@example.com");
        string cookie = browser.DefaultRequestHeaders.GetValues("Cookie").Single();

        using HttpClient copy = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        copy.DefaultRequestHeaders.Add("Cookie", cookie);

        HttpResponseMessage before = await copy.GetAsync("/api/v1/printers", TestContext.Current.CancellationToken);

        // Act
        using (browser)
        {
            string page = await browser.GetStringAsync("/", TestContext.Current.CancellationToken);

            using FormUrlEncodedContent logout = new(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page),
            });

            HttpResponseMessage signedOut = await browser.PostAsync("/Account/Logout", logout, TestContext.Current.CancellationToken);
            signedOut.StatusCode.Should().Be(HttpStatusCode.Redirect, "signing out is the setup here");
        }

        HttpResponseMessage after = await copy.GetAsync("/api/v1/printers", TestContext.Current.CancellationToken);

        // Assert
        before.StatusCode.Should().Be(HttpStatusCode.OK, "the copy is as good as the original while the session lasts");
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "signing out ended the session, not merely the browser's cookie");
    }
}
