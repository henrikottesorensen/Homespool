using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Mail;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// A password-reset link names the host the browser sent, never the one a header claimed - even
/// when the request came through the trusted proxy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the proxy has to be trusted for this to mean anything.</b> The forwarded-headers
/// middleware is not registered at all when nothing is trusted, so a test that sent the header to
/// an untrusting host would pass for the wrong reason. Here the peer is the configured proxy, and
/// <c>X-Forwarded-Proto</c> from that peer is honoured - the link says <c>https</c> - which is what
/// proves the middleware ran and chose not to read <c>X-Forwarded-Host</c>, rather than never
/// having looked.
/// </para>
/// <para>
/// <b>Why it is the reset link and not some page.</b> The header's whole value to an attacker is
/// the mail: a reset link that names their host delivers the token to them when the owner clicks
/// it. Host filtering never sees this header, because the middleware would rewrite
/// <c>Request.Host</c> after that check has already passed the real one.
/// </para>
/// </remarks>
public sealed class ForwardedHostTests : IAsyncLifetime
{
    private const string ProxyAddress = "172.28.0.2";
    private const string Address = "owner@example.com";
    private const string Password = "Correct-Horse-Battery-Staple-1!";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("forwarded-host");
    private readonly RecordingEmailSender _sender = new();
    private HomespoolFactory _root = null!;
    private WebApplicationFactory<Controllers.PrinterAppController> _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _root = new HomespoolFactory(_scratch);
        _root.ConfigurationOverrides["XForwarded:KnownProxies:0"] = ProxyAddress;

        // Configured but never contacted: a host makes the reset flow send mail rather than refuse
        // it, the recording sender below is what receives it, and the startup probe is off so
        // nothing tries to reach the name.
        _root.ConfigurationOverrides["Smtp:Host"] = "mail.example.invalid";
        _root.ConfigurationOverrides["Smtp:ProbeOnStartup"] = "false";

        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IEmailSender>(_sender);
            services.AddTransient<IStartupFilter>(_ => new FromTheProxy(IPAddress.Parse(ProxyAddress)));
        }));

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        await CreateConfirmedUserAsync(scope);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _root.DisposeAsync();

        _scratch.Dispose();
    }

    [Fact]
    public async Task TheResetLinkNamesTheHostTheBrowserSentAndNotTheForwardedOne()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "attacker.example");

        HttpResponseMessage page = await client.GetAsync("/Account/ForgotPassword", TestContext.Current.CancellationToken);
        string token = AntiforgeryTestHelper.ExtractToken(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using FormUrlEncodedContent body = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Email"] = Address,
        });

        // Act
        HttpResponseMessage answer = await client.PostAsync("/Account/ForgotPassword", body, TestContext.Current.CancellationToken);

        // Assert
        answer.StatusCode.Should().Be(HttpStatusCode.Redirect, "a known, confirmed address is sent on to the confirmation page");
        _sender.Sent.Should().ContainSingle("one reset mail goes to the address");

        string mail = _sender.Sent[0].body;

        mail.Should().Contain("https://localhost/Account/ResetPassword",
                              "the scheme comes from the trusted proxy's X-Forwarded-Proto and the host from the browser's own Host header");
        mail.Should().NotContain("attacker.example", "X-Forwarded-Host is not honoured, from the proxy or from anybody");
    }

    private static async Task CreateConfirmedUserAsync(IServiceScope scope)
    {
        IUserStore<HSUser> userStore = scope.ServiceProvider.GetRequiredService<IUserStore<HSUser>>();
        IUserEmailStore<HSUser> emailStore = (IUserEmailStore<HSUser>)userStore;
        UserManager<HSUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();

        HSUser user = new();
        await userStore.SetUserNameAsync(user, EnrolmentFlowHelper.UsernameFor(Address), CancellationToken.None);
        await emailStore.SetEmailAsync(user, Address, CancellationToken.None);
        user.EmailConfirmed = true;

        IdentityResult result = await userManager.CreateAsync(user, Password);
        result.Succeeded.Should().BeTrue("account creation is setup for this test, not what it verifies");
    }

    /// <summary>
    /// Makes every request arrive from the trusted proxy's address. TestServer accepts no
    /// connections, so the peer address is otherwise absent and the forwarded-headers middleware
    /// would trust nothing - the same seam <c>HomespoolFactory</c> uses for the listener port.
    /// </summary>
    private sealed class FromTheProxy(IPAddress peer) : IStartupFilter
    {
        public System.Action<IApplicationBuilder> Configure(System.Action<IApplicationBuilder> next)
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

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<(string email, string subject, string body)> Sent { get; } = [];

        public Task<EmailSendResult> SendEmailAsync(string email, string subject, string htmlMessage)
        {
            Sent.Add((email, subject, htmlMessage));

            return Task.FromResult(EmailSendResult.Sent);
        }
    }
}
