using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;

namespace Homespool.Host.E2ETest;

/// <summary>
/// That every response carries the four security headers, including the ones nothing in the
/// application deliberately produced.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of case, and the ordering bug is caught by the first rather than the second - which is
/// the reverse of what it looks like. A response with a body commits its headers as it writes, so
/// middleware setting them after <c>next</c> loses them on <c>/Account/Login</c> and <c>/health</c>;
/// a bodyless 404 or 401 still has mutable headers when control returns, and passes either way.
/// Verified by moving the write and watching which three fail. The 404 and 401 rows stay because
/// they cover responses no endpoint produced, which is a different claim and worth its own row.
/// </para>
/// <para>
/// Values are asserted, not merely presence. <c>DENY</c> weakened to <c>SAMEORIGIN</c>, or
/// <c>same-origin</c> relaxed to the browser default, would leave a test asserting presence
/// perfectly green while giving up the thing the header was added for.
/// </para>
/// </remarks>
public sealed class SecurityHeaderTests : IAsyncLifetime
{
    /// <summary>
    /// The exact shape: a script policy admitting this origin and one nonce, then the three
    /// directives that close the ways around it, and nothing about styles or connections - those are
    /// unrestricted on purpose, and a directive the policy does not name stays that way.
    /// </summary>
    private const string PolicyShape =
        @"^script-src 'self' 'nonce-[A-Za-z0-9_-]{22}'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'$";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("secheaders");
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

    [Theory]

    // A page that exists and renders.
    [InlineData("/Account/Login")]

    // Answered before any endpoint runs, which is what proves the headers are set on the way in.
    [InlineData("/no/such/path")]
    [InlineData("/api/v1/printers")]

    // Anonymous by design, and the one a monitoring system sees.
    [InlineData("/health")]
    public async Task EveryResponseCarriesTheSecurityHeaders(string url)
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        using HttpResponseMessage response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        // Assert
        Header(response, "X-Content-Type-Options").Should().Be("nosniff");
        Header(response, "X-Frame-Options").Should().Be("DENY");
        Header(response, "Content-Security-Policy").Should().MatchRegex(PolicyShape);
        Header(response, "Referrer-Policy").Should().Be("same-origin");
    }

    /// <summary>
    /// <b>The nonce is the policy's whole worth</b>, and it is worth nothing if it repeats: a value
    /// an attacker can predict is a value they can put on their own script. Two responses, two
    /// nonces.
    /// </summary>
    [Fact]
    public async Task EveryResponseMintsItsOwnNonce()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        using HttpResponseMessage first = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        using HttpResponseMessage second = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);

        // Assert
        Nonce(first).Should().NotBe(Nonce(second));
    }

    /// <summary>
    /// The one inline script carries the same nonce the header names, which is the only reason a
    /// browser runs it. A header and a page minted from different instances would agree on nothing,
    /// and the theme would silently stop following the OS.
    /// </summary>
    [Fact]
    public async Task TheInlineScriptCarriesTheHeadersNonce()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        using HttpResponseMessage response = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        string page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        page.Should().Contain($"<script nonce=\"{Nonce(response)}\">");
        page.Should().NotContain("<script>", "an inline block without the nonce is one the browser refuses");
    }

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
}
