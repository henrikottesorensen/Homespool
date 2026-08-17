using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;

using Homespool.Host.Services;

namespace Homespool.Host.Test;

/// <summary>
/// The first-run gate: while no administrator exists it must funnel navigable pages to <c>/setup</c>,
/// yet leave the setup page itself, the printer protocol, the API docs and static assets reachable -
/// and get out of the way entirely once setup completes.
/// </summary>
public class SetupGateMiddlewareTests
{
    private static async Task<(bool nextCalled, HttpContext context)> RunAsync(bool setupComplete, string path)
    {
        // Arrange
        SetupState state = new();
        state.Initialize(adminExists: setupComplete);

        SetupGateMiddleware middleware = new(state);

        DefaultHttpContext context = new();
        context.Request.Path = path;

        // A lambda rather than a local function purely so the unused HttpContext can stay a discard:
        // SA1313 exempts `_` for lambda parameters and nowhere else, which is a fair line - a
        // class-bound method has no business naming a parameter `_`.
        bool nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;

            return Task.CompletedTask;
        };

        // Act
        await middleware.InvokeAsync(context, next);

        return (nextCalled, context);
    }

    /// <summary>
    /// Once an administrator exists the gate is inert: even a page that would have been redirected now
    /// passes straight through with no 302.
    /// </summary>
    [Fact]
    public async Task CompletedSetupLetsEveryRequestThrough()
    {
        // Act
        (bool nextCalled, HttpContext context) = await RunAsync(setupComplete: true, path: "/Account/Login");

        // Assert
        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    /// <summary>
    /// While setup is pending, a human-facing page is redirected to <c>/setup</c> and not served.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/files")]
    [InlineData("/Account/Login")]
    [InlineData("/Account/Register")]
    [InlineData("/Account/Manage/Index")]
    public async Task PendingSetupRedirectsNavigablePagesToSetup(string path)
    {
        // Act
        (bool nextCalled, HttpContext context) = await RunAsync(setupComplete: false, path: path);

        // Assert
        nextCalled.Should().BeFalse("a gated page must not be served before an admin exists");
        context.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        context.Response.Headers.Location.ToString().Should().Be("/setup");
    }

    /// <summary>
    /// The allowlist that keeps the gate usable: the setup page itself, the printer protocol (which
    /// must never receive an HTML redirect), the dev API docs, and any static asset - identified by a
    /// file extension - so the setup page's own CSS and JS load.
    /// </summary>
    [Theory]
    [InlineData("/setup")]
    [InlineData("/p/register")]
    [InlineData("/p/ws")]
    [InlineData("/f/2a71b2bf1845a4752a033244cd856553/raw")]
    [InlineData("/scalar/v1")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/css/site.css")]
    [InlineData("/lib/bootstrap/dist/js/bootstrap.bundle.js")]
    [InlineData("/favicon.ico")]
    public async Task PendingSetupAllowsSetupProtocolDocsAndStaticAssets(string path)
    {
        // Act
        (bool nextCalled, HttpContext context) = await RunAsync(setupComplete: false, path: path);

        // Assert
        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Headers.Location.ToString().Should().BeEmpty();
    }
}
