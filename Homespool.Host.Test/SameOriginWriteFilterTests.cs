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
using Microsoft.Extensions.Primitives;

using NSubstitute;

using Homespool.Host.Authorisation;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="SameOriginWriteFilter"/> - a cookie-authenticated write is admitted on the browser's
/// word alone, and only when that word is <c>same-origin</c>.
/// </summary>
/// <remarks>
/// The rule is a pure function of three things, so the first half drives it directly and each row
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
