using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Primitives;

using NSubstitute;

using Homespool.Host.Authorisation;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="SameOriginWriteFilter"/> - a cookie-authenticated write is admitted on the browser's
/// word alone, and only when that word is <c>same-origin</c>.
/// </summary>
/// <remarks>
/// The rule is a pure function of its inputs, so the first half drives it directly and each row
/// that refuses is paired with the nearest row that admits, so a change to any one input is caught by
/// the case that names it. The second half goes through the filter with the cookie scheme stubbed,
/// which is where the two decisions the rule cannot see live: that the cookie is asked rather than
/// the principal, and that a Razor page is not a controller.
/// </remarks>
public class SameOriginWriteFilterTests
{
    /// <summary>The case it exists for: the cookie, a write, and no word from the browser.</summary>
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void ACookieWriteWithoutTheHeaderIsRefused(string method)
    {
        SameOriginWriteFilter.Refuses(method, StringValues.Empty, cookieAuthenticated: true).Should().BeTrue();
    }

    /// <summary>What the site's own script produces: the browser attributes the write to this origin.</summary>
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void ACookieWriteFromThisOriginIsAdmitted(string method)
    {
        SameOriginWriteFilter.Refuses(method, SameOriginWriteFilter.SameOrigin, cookieAuthenticated: true).Should().BeFalse();
    }

    /// <summary>
    /// <b><c>same-site</c> is the gap this closes</b>: a sibling subdomain or another port on this
    /// host, which <c>SameSite=Lax</c> lets through. <c>none</c> and <c>cross-site</c> cannot come
    /// from a page on this origin at all, and the comparison is exact.
    /// </summary>
    [Theory]
    [InlineData("same-site")]
    [InlineData("cross-site")]
    [InlineData("none")]
    [InlineData("Same-Origin")]
    [InlineData("")]
    public void ACookieWriteTheBrowserDoesNotAttributeToThisOriginIsRefused(string secFetchSite)
    {
        SameOriginWriteFilter.Refuses("POST", secFetchSite, cookieAuthenticated: true).Should().BeTrue();
    }

    /// <summary>Two values is a request somebody assembled by hand, not a browser's.</summary>
    [Fact]
    public void ACookieWriteWithTwoHeaderValuesIsRefused()
    {
        StringValues two = new([SameOriginWriteFilter.SameOrigin, SameOriginWriteFilter.SameOrigin]);

        SameOriginWriteFilter.Refuses("POST", two, cookieAuthenticated: true).Should().BeTrue();
    }

    /// <summary>Reads are never refused: a cross-site GET carries no cookie, and every API GET is a read.</summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void AReadIsAdmittedWithoutTheHeader(string method)
    {
        SameOriginWriteFilter.Refuses(method, StringValues.Empty, cookieAuthenticated: true).Should().BeFalse();
    }

    /// <summary>
    /// No cookie, nothing to refuse: a token is placed by whoever holds it, so a foreign page cannot
    /// spend it, and an anonymous write is somebody else's refusal.
    /// </summary>
    [Fact]
    public void AWriteTheCookieDidNotAuthenticateIsAdmittedWithoutTheHeader()
    {
        SameOriginWriteFilter.Refuses("PUT", StringValues.Empty, cookieAuthenticated: false).Should().BeFalse();
    }

    private const string PlainOrigin = "http://192.0.2.10:8080";

    /// <summary>
    /// <b>Over plain HTTP no browser sends the header</b>, to its own origin included, so the
    /// browser's <c>Origin</c> stands in - and admits when it names exactly this origin.
    /// </summary>
    [Theory]
    [InlineData("POST", PlainOrigin)]
    [InlineData("DELETE", PlainOrigin)]
    [InlineData("POST", "HTTP://192.0.2.10:8080")]
    public void APlainHttpCookieWriteWithoutTheHeaderFromThisOriginIsAdmitted(string method, string origin)
    {
        SameOriginWriteFilter.Refuses(method, StringValues.Empty, cookieAuthenticated: true, origin, PlainOrigin).Should().BeFalse();
    }

    /// <summary>
    /// The sibling port and subdomain are the same-site gap again, and name themselves in
    /// <c>Origin</c>; <c>null</c> is an opaque origin, and absent is no word at all.
    /// </summary>
    [Theory]
    [InlineData("http://192.0.2.10:8081")]
    [InlineData("http://192.0.2.10")]
    [InlineData("https://192.0.2.10:8080")]
    [InlineData("http://other.192.0.2.10:8080")]
    [InlineData("http://192.0.2.10:8080/")]
    [InlineData("null")]
    [InlineData("")]
    public void APlainHttpCookieWriteWithoutTheHeaderFromAnotherOriginIsRefused(string origin)
    {
        SameOriginWriteFilter.Refuses("POST", StringValues.Empty, cookieAuthenticated: true, origin, PlainOrigin).Should().BeTrue();
    }

    /// <summary>No <c>Origin</c> at all, and two of them, are refused like the header's own edges.</summary>
    [Fact]
    public void APlainHttpCookieWriteWithNoneOrTwoOriginsIsRefused()
    {
        StringValues two = new([PlainOrigin, PlainOrigin]);

        SameOriginWriteFilter.Refuses("POST", StringValues.Empty, cookieAuthenticated: true, StringValues.Empty, PlainOrigin).Should().BeTrue();
        SameOriginWriteFilter.Refuses("POST", StringValues.Empty, cookieAuthenticated: true, two, PlainOrigin).Should().BeTrue();
    }

    /// <summary>
    /// <b>Over HTTPS <c>Origin</c> is never consulted</b>: there absent means a browser too old to
    /// send the header, and the floor stays the header.
    /// </summary>
    [Fact]
    public void AnHttpsCookieWriteWithoutTheHeaderIsRefusedWhateverItsOrigin()
    {
        SameOriginWriteFilter.Refuses("POST", StringValues.Empty, cookieAuthenticated: true, "https://homespool.example", plainHttpOrigin: null)
                             .Should().BeTrue();
    }

    /// <summary><b>A header that is present is believed</b>, even where <c>Origin</c> would have admitted.</summary>
    [Fact]
    public void APlainHttpCookieWriteTheHeaderRefusesIsNotRescuedByItsOrigin()
    {
        SameOriginWriteFilter.Refuses("POST", "same-site", cookieAuthenticated: true, PlainOrigin, PlainOrigin).Should().BeTrue();
    }

    /// <summary>The origin is the browser's spelling of the request's own: scheme, host and any port.</summary>
    [Theory]
    [InlineData("192.0.2.10:8080", "http://192.0.2.10:8080")]
    [InlineData("homespool.lan", "http://homespool.lan")]
    [InlineData("[2001:db8::1]:8080", "http://[2001:db8::1]:8080")]
    public void APlainHttpRequestsOriginIsBuiltFromItsHost(string host, string expected)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.Scheme = Uri.UriSchemeHttp;
        httpContext.Request.Host = new HostString(host);

        SameOriginWriteFilter.PlainHttpOrigin(httpContext.Request).Should().Be(expected);
    }

    /// <summary>Over HTTPS there is no plain origin to compare with, and a request naming no host has none either.</summary>
    [Fact]
    public void AnHttpsRequestOrOneWithoutAHostHasNoPlainOrigin()
    {
        DefaultHttpContext https = new();
        https.Request.Scheme = Uri.UriSchemeHttps;
        https.Request.Host = new HostString("192.0.2.10:8080");

        DefaultHttpContext hostless = new();
        hostless.Request.Scheme = Uri.UriSchemeHttp;

        SameOriginWriteFilter.PlainHttpOrigin(https.Request).Should().BeNull();
        SameOriginWriteFilter.PlainHttpOrigin(hostless.Request).Should().BeNull();
    }

    private static AuthorizationFilterContext Context(ActionDescriptor action,
                                                      string method,
                                                      AuthenticateResult byCookie,
                                                      string? secFetchSite = null)
    {
        IAuthenticationService authentication = Substitute.For<IAuthenticationService>();
        authentication.AuthenticateAsync(Arg.Any<HttpContext>(), IdentityConstants.ApplicationScheme).Returns(byCookie);

        DefaultHttpContext httpContext = new()
        {
            RequestServices = new ServiceCollection().AddSingleton(authentication).BuildServiceProvider(),
        };
        httpContext.Request.Method = method;
        httpContext.Request.Path = "/api/v1/files/model.gcode";

        if (secFetchSite is not null)
        {
            httpContext.Request.Headers[SameOriginWriteFilter.HeaderName] = secFetchSite;
        }

        return new AuthorizationFilterContext(new ActionContext(httpContext, new RouteData(), action), new List<IFilterMetadata>());
    }

    private static AuthenticateResult CookieSucceeded()
    {
        ClaimsPrincipal principal = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], IdentityConstants.ApplicationScheme));

        return AuthenticateResult.Success(new AuthenticationTicket(principal, IdentityConstants.ApplicationScheme));
    }

    private static SameOriginWriteFilter Filter()
    {
        return new SameOriginWriteFilter(NullLogger<SameOriginWriteFilter>.Instance);
    }

    /// <summary>The refusal is a 403 problem, not a redirect and not a 500.</summary>
    [Fact]
    public async Task ARefusalIsA403Problem()
    {
        // Arrange
        AuthorizationFilterContext context = Context(new ControllerActionDescriptor(), "PUT", CookieSucceeded());

        // Act
        await Filter().OnAuthorizationAsync(context);

        // Assert
        ObjectResult result = context.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        result.Value.Should().BeOfType<ProblemDetails>().Which.Status.Should().Be(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// The refusal names the header's value, which only a browser is obliged to write honestly - so
    /// it is logged cleaned and cut. The path beside it is a <c>PathString</c>, which renders escaped.
    /// </summary>
    [Fact]
    public async Task ARefusalLogsTheHeaderCleaned()
    {
        // Arrange
        FakeLogger<SameOriginWriteFilter> logger = new();
        AuthorizationFilterContext context = Context(new ControllerActionDescriptor(),
                                                     "PUT",
                                                     CookieSucceeded(),
                                                     "cross-site\u001B[2J" + new string('x', 500));
        context.HttpContext.Request.Path = "/api/v1/files/a\u001B[2J.gcode";

        // Act
        await new SameOriginWriteFilter(logger).OnAuthorizationAsync(context);

        // Assert
        FakeLogRecord refusal = logger.Collector.GetSnapshot().Should().ContainSingle().Subject;

        refusal.StructuredState.Should().Contain(
            pair => pair.Key == "SecFetchSite" &&
                    pair.Value!.StartsWith("cross-site\uFFFD[2Jx", StringComparison.Ordinal) &&
                    pair.Value.EndsWith("<514 characters in all>", StringComparison.Ordinal));
        refusal.StructuredState.Should().Contain(pair => pair.Key == "Path" && pair.Value == "/api/v1/files/a%1B%5B2J.gcode");
    }

    /// <summary>
    /// The refusal names the scheme, which says which rule applied, and the <c>Origin</c> - cleaned
    /// and cut like the header, since only a browser is obliged to write it honestly.
    /// </summary>
    [Fact]
    public async Task ARefusalLogsTheSchemeAndTheOriginCleaned()
    {
        // Arrange
        FakeLogger<SameOriginWriteFilter> logger = new();
        AuthorizationFilterContext context = Context(new ControllerActionDescriptor(), "PUT", CookieSucceeded());
        context.HttpContext.Request.Scheme = Uri.UriSchemeHttp;
        context.HttpContext.Request.Host = new HostString("192.0.2.10:8080");
        context.HttpContext.Request.Headers[SameOriginWriteFilter.OriginHeaderName] = "http://evil\u001B[2J" + new string('x', 500);

        // Act
        await new SameOriginWriteFilter(logger).OnAuthorizationAsync(context);

        // Assert
        FakeLogRecord refusal = logger.Collector.GetSnapshot().Should().ContainSingle().Subject;

        refusal.StructuredState.Should().Contain(pair => pair.Key == "Scheme" && pair.Value == Uri.UriSchemeHttp);
        refusal.StructuredState.Should().Contain(pair => pair.Key == "SecFetchSite" && pair.Value == "absent");
        refusal.StructuredState.Should().Contain(
            pair => pair.Key == "Origin" &&
                    pair.Value!.StartsWith("http://evil\uFFFD[2Jx", StringComparison.Ordinal) &&
                    pair.Value.EndsWith("<515 characters in all>", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>The request's own origin is what the filter compares with</b>: a plain-HTTP write whose
    /// <c>Origin</c> names this host is admitted through the filter, not only by the rule.
    /// </summary>
    [Fact]
    public async Task APlainHttpWriteFromThisOriginIsAdmittedThroughTheFilter()
    {
        // Arrange
        AuthorizationFilterContext context = Context(new ControllerActionDescriptor(), "POST", CookieSucceeded());
        context.HttpContext.Request.Scheme = Uri.UriSchemeHttp;
        context.HttpContext.Request.Host = new HostString("192.0.2.10:8080");
        context.HttpContext.Request.Headers[SameOriginWriteFilter.OriginHeaderName] = PlainOrigin;

        // Act
        await Filter().OnAuthorizationAsync(context);

        // Assert
        context.Result.Should().BeNull();
    }

    /// <summary>The same write over HTTPS is refused: there the header is the only word taken.</summary>
    [Fact]
    public async Task AnHttpsWriteFromThisOriginWithoutTheHeaderIsRefusedThroughTheFilter()
    {
        // Arrange
        AuthorizationFilterContext context = Context(new ControllerActionDescriptor(), "POST", CookieSucceeded());
        context.HttpContext.Request.Scheme = Uri.UriSchemeHttps;
        context.HttpContext.Request.Host = new HostString("192.0.2.10:8080");
        context.HttpContext.Request.Headers[SameOriginWriteFilter.OriginHeaderName] = "https://192.0.2.10:8080";

        // Act
        await Filter().OnAuthorizationAsync(context);

        // Assert
        context.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    /// <summary>And an admitted request leaves no result behind, so the action runs.</summary>
    [Fact]
    public async Task AnAdmittedRequestSetsNoResult()
    {
        // Arrange
        AuthorizationFilterContext context = Context(
            new ControllerActionDescriptor(), "PUT", CookieSucceeded(), SameOriginWriteFilter.SameOrigin);

        // Act
        await Filter().OnAuthorizationAsync(context);

        // Assert
        context.Result.Should().BeNull();
    }

    /// <summary>
    /// <b>The cookie scheme is asked, not the principal.</b> A token-authenticated identity carries
    /// the cookie scheme's name too, because both go through the same claims factory; only the
    /// scheme's own answer tells them apart.
    /// </summary>
    [Fact]
    public async Task AWriteTheCookieSchemeDidNotAuthenticateIsAdmitted()
    {
        // Arrange
        AuthorizationFilterContext context = Context(new ControllerActionDescriptor(), "PUT", AuthenticateResult.NoResult());
        context.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], IdentityConstants.ApplicationScheme));

        // Act
        await Filter().OnAuthorizationAsync(context);

        // Assert
        context.Result.Should().BeNull();
    }

    /// <summary>
    /// <b>A Razor page is left alone</b>: global MVC filters reach pages too, and a page's write is
    /// already proven by its antiforgery token.
    /// </summary>
    [Fact]
    public async Task ARazorPageIsNotAControllerAndIsLeftAlone()
    {
        // Arrange
        AuthorizationFilterContext context = Context(new PageActionDescriptor(), "POST", CookieSucceeded());

        // Act
        await Filter().OnAuthorizationAsync(context);

        // Assert
        context.Result.Should().BeNull();
    }

    /// <summary>A read never asks the cookie scheme at all - the cheapest path, and the common one.</summary>
    [Fact]
    public async Task AReadNeverAsksTheCookieScheme()
    {
        // Arrange
        AuthorizationFilterContext context = Context(new ControllerActionDescriptor(), "GET", CookieSucceeded());

        // Act
        await Filter().OnAuthorizationAsync(context);

        // Assert
        context.Result.Should().BeNull();
        await context.HttpContext.RequestServices.GetRequiredService<IAuthenticationService>()
                     .DidNotReceiveWithAnyArgs().AuthenticateAsync(default!, default);
    }
}
