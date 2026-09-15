using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// A request that fails is answered, for a person at a browser, with the error page and the trace id
/// that finds the failure in the log; everything machine-facing keeps the bare 500; and Development
/// keeps the framework's developer exception page.
/// </summary>
/// <remarks>
/// <b>Each test starts only the host it asks for.</b> The handler is registered outside Development,
/// and <c>WebApplicationFactory</c> runs as Development, so the Production hosts set the environment
/// explicitly - and a derived factory is a host of its own, with its own <see cref="SetupState"/>.
/// </remarks>
public sealed partial class ErrorPageTests : IAsyncLifetime
{
    private const string Password = "Correct-Horse-Battery-Staple-1!"; // betterleaks:allow
    private const string Address = "no-authenticator@example.com";

    private const string PolicyShape =
        @"^script-src 'self' 'nonce-[A-Za-z0-9_-]{22}'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'$";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("errorpage");
    private readonly List<IAsyncDisposable> _hosts = [];
    private HomespoolFactory _root = null!;

    public ValueTask InitializeAsync()
    {
        _root = new HomespoolFactory(_scratch);

        // Inert for every test that signs nobody in, and what the enrolment exemption is tested under.
        _root.ConfigurationOverrides["Security:RequireTwoFactor"] = "true";

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IAsyncDisposable host in _hosts)
        {
            await host.DisposeAsync();
        }

        await _root.DisposeAsync();

        _scratch.Dispose();
    }

    /// <summary>
    /// The page, its reference, and the security headers the handler's clearing of the response would
    /// have removed - on a POST as well, which the handler re-runs as a POST.
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task AFailureIsAnsweredWithTheErrorPageAndItsReference(string method)
    {
        // Arrange
        using HttpClient client = Host(Environments.Production).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpRequestMessage request = new(new HttpMethod(method), "/test-only/throws");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        string page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "the page describes the failure, it does not replace it");
        page.Should().Contain("Something went wrong");
        TraceReference().IsMatch(page).Should().BeTrue("the reference is the bare 32-character trace id the log carries as @tr");
        page.Should().NotContain(ThrowingController.Message, "outside Development the exception stays in the log");

        Header(response, "X-Content-Type-Options").Should().Be("nosniff");
        Header(response, "X-Frame-Options").Should().Be("DENY");
        Header(response, "Referrer-Policy").Should().Be("same-origin");
        Header(response, "Content-Security-Policy").Should().MatchRegex(PolicyShape);
        page.Should().Contain($"<script nonce=\"{Nonce(response)}\">", "the layout's inline block has to run under the page's own policy");
    }

    /// <summary>A script, PrusaSlicer or a printer is handed the empty 500 it can read, not a page of HTML.</summary>
    /// <remarks>
    /// <b>The exception reaching the caller is the bare 500.</b> Kestrel answers an exception nothing
    /// handled with an empty 500; <c>TestServer</c> hands the same exception back to the client
    /// instead, so arriving here intact is what proves no handler took it.
    /// </remarks>
    [Theory]
    [InlineData("/api/test-only/throws")]
    [InlineData("/compat/test-only/throws")]
    public async Task AMachineFacingFailureKeepsTheBare500(string path)
    {
        // Arrange
        using HttpClient client = Host(Environments.Production).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        Func<Task> act = () => client.GetAsync(path, TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(ThrowingController.Message);
    }

    /// <summary>With no failure behind it there is nothing to describe.</summary>
    [Fact]
    public async Task TheErrorPageOpenedDirectlyIsNotFound()
    {
        // Arrange
        using HttpClient client = Host(Environments.Production).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        using HttpResponseMessage response = await client.GetAsync("/Error", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// An account held for enrolment still reaches the error page: a failure re-run as it and
    /// redirected to enrolment would show the account the wrong page, or loop when enrolment failed.
    /// Opened directly, reaching it is the 404 rather than a redirect.
    /// </summary>
    [Fact]
    public async Task AnAccountHeldForEnrolmentStillReachesTheErrorPage()
    {
        // Arrange
        WebApplicationFactory<Controllers.PrinterAppController> host = Host(Environments.Production);
        using HttpClient client = await SignedInWithNoAuthenticatorAsync(host);

        // Act
        using HttpResponseMessage response = await client.GetAsync("/Error", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "the enrolment gate let the request through to the page");
    }

    /// <summary>
    /// Development keeps the framework's page, exception and all, and the script policy steps aside
    /// for it - its one inline block carries no nonce.
    /// </summary>
    [Fact]
    public async Task DevelopmentShowsTheFrameworksExceptionPage()
    {
        // Arrange
        using HttpClient client = Host(Environments.Development).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpRequestMessage request = new(HttpMethod.Get, "/test-only/throws");
        request.Headers.Accept.ParseAdd("text/html");

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        string page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        page.Should().Contain(ThrowingController.Message).And.NotContain("Something went wrong");
        Header(response, "Content-Security-Policy").Should().Be("object-src 'none'; base-uri 'self'; frame-ancestors 'none'");
        Header(response, "X-Content-Type-Options").Should().Be("nosniff");
    }

    [GeneratedRegex("<code>[0-9a-f]{32}</code>")]
    private static partial Regex TraceReference();

    private static string Nonce(HttpResponseMessage response)
    {
        string policy = Header(response, "Content-Security-Policy");
        int start = policy.IndexOf("'nonce-", StringComparison.Ordinal) + "'nonce-".Length;
        int end = policy.IndexOf('\'', start);

        return policy[start..end];
    }

    private static string Header(HttpResponseMessage response, string name)
    {
        response.Headers.TryGetValues(name, out IEnumerable<string>? values).Should().BeTrue($"{name} should be present");

        return string.Join(",", values!);
    }

    private static async Task<HttpClient> SignedInWithNoAuthenticatorAsync(WebApplicationFactory<Controllers.PrinterAppController> host)
    {
        using (IServiceScope scope = host.Services.CreateScope())
        {
            IUserStore<HSUser> store = scope.ServiceProvider.GetRequiredService<IUserStore<HSUser>>();
            UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

            HSUser user = new();
            await store.SetUserNameAsync(user, "noauth", CancellationToken.None);
            await ((IUserEmailStore<HSUser>)store).SetEmailAsync(user, Address, CancellationToken.None);
            user.EmailConfirmed = true;

            (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue("the account is setup for this test, not what it verifies");
        }

        HttpClient client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        string login = await client.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["Input.Login"] = Address,
            ["Input.Password"] = Password,
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(login),
        });

        using HttpResponseMessage signIn = await client.PostAsync("/Account/Login", body, TestContext.Current.CancellationToken);
        signIn.StatusCode.Should().Be(HttpStatusCode.Redirect, "signing in is setup for this test, not what it verifies");

        using HttpResponseMessage held = await client.GetAsync("/Printers", TestContext.Current.CancellationToken);
        held.Headers.Location!.OriginalString.Should().Contain("EnableAuthenticator", "the account has to be held for the test to mean anything");

        return client;
    }

    /// <summary>A host in <paramref name="environment"/> carrying <see cref="ThrowingController"/>, with setup complete.</summary>
    private WebApplicationFactory<Controllers.PrinterAppController> Host(string environment)
    {
        WebApplicationFactory<Controllers.PrinterAppController> host = _root.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureTestServices(services => services.AddControllers().AddApplicationPart(typeof(ThrowingController).Assembly));
        });

        _hosts.Add(host);

        using IServiceScope scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return host;
    }
}
