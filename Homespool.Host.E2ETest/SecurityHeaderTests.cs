using System;
using System.Collections.Generic;
using System.IO;
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
public sealed class SecurityHeaderTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-secheaders-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
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
        Header(response, "Content-Security-Policy").Should().Be("frame-ancestors 'none'");
        Header(response, "Referrer-Policy").Should().Be("same-origin");
    }

    /// <summary>
    /// The policy names <c>frame-ancestors</c> and nothing else, deliberately: a directive a policy
    /// does not mention stays unrestricted, which is what lets framing be closed without taking on
    /// the inline script and two CDNs that a script-src would have to account for.
    /// </summary>
    [Fact]
    public async Task ThePolicyRestrictsFramingAndNothingElse()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        using HttpResponseMessage response = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);

        // Assert
        string policy = Header(response, "Content-Security-Policy");

        policy.Should().NotContain("script-src", "a script policy needs a nonce for the colour-mode block, and is its own change");
        policy.Should().NotContain("style-src");
        policy.Should().Contain("frame-ancestors 'none'");
    }

    private static string Header(HttpResponseMessage response, string name)
    {
        response.Headers.TryGetValues(name, out IEnumerable<string>? values).Should().BeTrue($"{name} should be present");

        return string.Join(",", values!);
    }
}
