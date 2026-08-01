using System.Threading.Tasks;

using AwesomeAssertions;
using Homespool.Host.Listeners;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Test;

/// <summary>
/// The listener boundary itself: which listener a route belongs to, and what happens to a request
/// that reaches it on the other one.
/// </summary>
/// <remarks>
/// The companion to <c>RouteListenerSegregationTests</c>, which asserts the same rule against the
/// application's real endpoint list. This file is about the rule; that one is about whether every
/// route is actually covered by it.
/// </remarks>
public class ListenerSegregationTests
{
    private const int PrinterPort = 15443;
    private const int UserPort = 8080;

    /// <summary>
    /// The printer protocol lives under <c>/p</c>, and nothing else does. Both halves matter: a page
    /// whose name merely starts with a p must not be dragged onto the printer listener.
    /// </summary>
    [Theory]
    [InlineData("p/ws", ListenerClass.Printer)]
    [InlineData("/p/register", ListenerClass.Printer)]
    [InlineData("P/Camera", ListenerClass.Printer)]
    [InlineData("p", ListenerClass.Printer)]
    [InlineData("printers/add", ListenerClass.User)]
    [InlineData("api/v1/printers", ListenerClass.User)]
    [InlineData("health/live", ListenerClass.User)]
    [InlineData("", ListenerClass.User)]
    public void RoutesAreClassifiedByTheirFirstSegment(string routePattern, ListenerClass expected)
    {
        ListenerSegregation.ClassFor(routePattern).Should().Be(expected);
    }

    /// <summary>
    /// A printer endpoint reached on the printer listener is served, which is the case everything
    /// else here is measured against.
    /// </summary>
    [Fact]
    public async Task APrinterEndpointOnThePrinterListenerIsServed()
    {
        // Act
        (bool nextCalled, HttpContext context) = await RunAsync(ListenerClass.Printer, arrivedOnPort: PrinterPort);

        // Assert
        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    /// <summary>
    /// <b>The leak this whole mechanism exists to stop.</b> A printer route reachable on the user
    /// listener would put <c>/p/ws</c> behind the proxy, where forwarded headers are trusted - so a
    /// stolen printer token would work from anywhere a browser can reach, and the segregation would be
    /// a comment rather than a boundary.
    /// </summary>
    [Fact]
    public async Task APrinterEndpointOnTheUserListenerIsRefused()
    {
        // Act
        (bool nextCalled, HttpContext context) = await RunAsync(ListenerClass.Printer, arrivedOnPort: UserPort);

        // Assert
        nextCalled.Should().BeFalse("the endpoint must not run at all, not merely fail afterwards");
        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound,
            "404 rather than 403: on this listener the route genuinely does not exist, and 403 would "
            + "confirm to whoever is probing that it exists elsewhere");
    }

    /// <summary>
    /// The other direction: a leaked printer token must not reach any application surface, so the
    /// user's routes are absent from the printer listener too.
    /// </summary>
    [Fact]
    public async Task AUserEndpointOnThePrinterListenerIsRefused()
    {
        // Act
        (bool nextCalled, HttpContext context) = await RunAsync(ListenerClass.User, arrivedOnPort: PrinterPort);

        // Assert
        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// A port belonging to neither listener is not a printer. Unreachable today - Kestrel binds only
    /// what <see cref="ListenerOptions"/> names - but it pins the direction the classification fails
    /// in, which is the property that matters if a listener is ever added.
    /// </summary>
    [Fact]
    public async Task AnUnrecognisedPortNeverServesPrinterEndpoints()
    {
        // Act
        (bool nextCalled, HttpContext context) = await RunAsync(ListenerClass.Printer, arrivedOnPort: 9999);

        // Assert
        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// An endpoint nobody classified is classified anyway, from the path it was reached at - so a
    /// <c>/p/</c> route that escaped the convention is still refused on the user listener.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the case that decides whether "forgot to segregate a route" is a leak or a nuisance,
    /// and it is not hypothetical: <c>MapStaticAssets</c> adds a file fallback endpoint outside the
    /// builder it returns, so at least one endpoint can never carry the metadata.
    /// </para>
    /// <para>
    /// Refusing everything unclassified was the first design and would have 404ed that fallback on
    /// every static-file request; treating it as user-facing would have served an unclassified
    /// <c>/p/</c> route to the whole internet. Falling back to the same prefix rule, applied to the
    /// request instead of the route, does neither.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnUnclassifiedEndpointFallsBackToTheRuleItsPathImplies()
    {
        // Act - the request path is /p/ws in every case here, so an unclassified endpoint is a
        // printer endpoint that escaped the convention.
        (bool onPrinter, HttpContext printerContext) = await RunAsync(requirement: null, arrivedOnPort: PrinterPort);
        (bool onUser, HttpContext userContext) = await RunAsync(requirement: null, arrivedOnPort: UserPort);

        // Assert
        onPrinter.Should().BeTrue("the path says this belongs to the printer listener, and that is where it arrived");
        printerContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

        onUser.Should().BeFalse("a /p/ route missing its metadata must still not appear on the user listener");
        userContext.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// And an unclassified endpoint that is <i>not</i> under <c>/p</c> stays served, which is what
    /// keeps the framework's own file fallback working.
    /// </summary>
    [Fact]
    public async Task AnUnclassifiedUserPathIsStillServedToUsers()
    {
        // Act
        (bool nextCalled, HttpContext context) = await RunAsync(
            requirement: null, arrivedOnPort: UserPort, path: "/site.webmanifest");

        // Assert
        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    /// <summary>
    /// A request that matched no route at all is left alone - the 404 is routing's to write, and
    /// nothing about it concerns listeners.
    /// </summary>
    [Fact]
    public async Task ARequestThatMatchedNoEndpointPassesThrough()
    {
        // Act
        (bool nextCalled, HttpContext context) = await RunAsync(requirement: null, arrivedOnPort: UserPort, matched: false);

        // Assert
        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    private static Task<(bool nextCalled, HttpContext context)> RunAsync(ListenerClass requirement, int arrivedOnPort) =>
        RunAsync((ListenerClass?)requirement, arrivedOnPort);

    private static async Task<(bool nextCalled, HttpContext context)> RunAsync(ListenerClass? requirement,
                                                                               int arrivedOnPort,
                                                                               bool matched = true,
                                                                               string path = "/p/ws")
    {
        // Arrange
        ListenerSegregationMiddleware middleware = new(
            Options.Create(new ListenerOptions { PrinterPort = PrinterPort, UserPort = UserPort }),
            NullLogger<ListenerSegregationMiddleware>.Instance);

        DefaultHttpContext context = new();
        context.Request.Path = path;
        context.Connection.LocalPort = arrivedOnPort;

        if (matched)
        {
            EndpointMetadataCollection metadata = requirement is null
                ? new EndpointMetadataCollection()
                : new EndpointMetadataCollection(new ListenerRequirement(requirement.Value));

            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test endpoint"));
        }

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
}
