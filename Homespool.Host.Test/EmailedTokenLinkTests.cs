using System;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;

using NSubstitute;

using Homespool.Host.Accounts;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="EmailedToken.Link"/> asks for an absolute link on the request's own scheme, and throws
/// rather than mailing an empty one.
/// </summary>
/// <remarks>
/// A substitute stands in for routing: what is under test is what the helper asks the URL helper for
/// and what it does with the answer, and the captured route context says the first more legibly than
/// a matcher would.
/// </remarks>
public sealed class EmailedTokenLinkTests
{
    /// <summary>The page, its values and the request's scheme all reach the URL helper, and its answer comes back.</summary>
    [Fact]
    public void ALinkIsAskedForOnTheRequestsScheme()
    {
        UrlRouteContext? asked = null;
        IUrlHelper url = NewUrlHelper("https");
        url.RouteUrl(Arg.Any<UrlRouteContext>())
           .Returns(call =>
           {
               asked = call.Arg<UrlRouteContext>();
               return "https://homespool.example.net/Account/ConfirmEmail?code=abc";
           });

        string link = EmailedToken.Link(url, "/Account/ConfirmEmail", new { code = "abc" });

        link.Should().Be("https://homespool.example.net/Account/ConfirmEmail?code=abc");
        asked.Should().NotBeNull();
        asked!.Protocol.Should().Be("https");
        RouteValueDictionary values = new(asked.Values);
        values["page"].Should().Be("/Account/ConfirmEmail");
        values["code"].Should().Be("abc");
    }

    /// <summary>A page that does not resolve throws, naming the page, instead of mailing a link to nothing.</summary>
    [Fact]
    public void APageThatDoesNotResolveThrows()
    {
        IUrlHelper url = NewUrlHelper("https");
        url.RouteUrl(Arg.Any<UrlRouteContext>()).Returns((string?)null);

        Action build = () => EmailedToken.Link(url, "/Account/Renamed", new { code = "abc" });

        build.Should().Throw<InvalidOperationException>().WithMessage("*'/Account/Renamed'*");
    }

    private static IUrlHelper NewUrlHelper(string scheme)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.Scheme = scheme;
        httpContext.Request.Host = new HostString("homespool.example.net");

        IUrlHelper url = Substitute.For<IUrlHelper>();
        url.ActionContext.Returns(new ActionContext(httpContext, new RouteData(), new PageActionDescriptor()));

        return url;
    }
}
