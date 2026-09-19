using System;
using System.Security.Claims;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Homespool.Host.PrintFiles;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="BoundedUploadAttribute"/>'s two forms, and that they bound different quantities.
/// </summary>
/// <remarks>
/// Driven through <see cref="IFilterFactory.CreateInstance"/> and <see cref="IAuthorizationFilter"/>
/// rather than the filter class, which is internal - that is also exactly how MVC reaches it, so the
/// test exercises the public contract instead of a shape only a test can see. The *ordering* half of
/// the attribute is not testable here and never was; <c>FilesPageUploadLimitTests</c> drives a real
/// host for it.
/// </remarks>
public sealed class BoundedUploadAttributeTests
{
    private const long ConfiguredCap = 4096;

    /// <summary>
    /// A ceiling written into the declaration is the request's own and is applied as written -
    /// nothing added. An endpoint that says 2048 and gets 2048 plus an overhead it never asked for is
    /// the surprise the second form exists to avoid.
    /// </summary>
    [Fact]
    public void ADeclaredCeilingIsAppliedExactly()
    {
        // Arrange
        FakeMaxRequestBodySizeFeature feature = new();
        AuthorizationFilterContext context = ContextWith(feature);

        // Act
        FilterFor(new BoundedUploadAttribute(2048)).OnAuthorization(context);

        // Assert
        feature.MaxRequestBodySize.Should().Be(2048);
    }

    /// <summary>
    /// The configured form describes a <i>file</i>, so the request ceiling has to leave room for the
    /// framing and fields travelling beside it. Asserted as "more than the cap" rather than against a
    /// copied constant: the property under test is that room is added, and a second copy of the
    /// figure would be one more thing able to disagree with the first.
    /// </summary>
    [Fact]
    public void TheConfiguredFormLeavesRoomBesideTheFile()
    {
        // Arrange
        FakeMaxRequestBodySizeFeature feature = new();
        AuthorizationFilterContext context = ContextWith(feature);

        // Act
        FilterFor(new BoundedUploadAttribute()).OnAuthorization(context);

        // Assert
        feature.MaxRequestBodySize.Should().BeGreaterThan(ConfiguredCap, "the form travels with the file");
        feature.MaxRequestBodySize.Should().BeLessThan(ConfiguredCap + (1024 * 1024),
                                                       "room for a form, not a second upload");
    }

    /// <summary>
    /// A declared ceiling of zero or less would be a limit that refuses everything, which reads as a
    /// disabled bound rather than a strict one. Refused where it is written instead.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void ADeclaredCeilingMustBePositive(long declared)
    {
        FluentActions.Invoking(() => new BoundedUploadAttribute(declared))
                     .Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// The feature is absent on some servers - <c>TestServer</c> has none - and the filter must leave
    /// the request alone rather than fault, since the multipart limit it also sets still bounds it.
    /// </summary>
    [Fact]
    public void AnAbsentBodySizeFeatureIsNotAnError()
    {
        // Arrange - no IHttpMaxRequestBodySizeFeature set on the context at all.
        AuthorizationFilterContext context = ContextWith(null);

        // Act & Assert
        FluentActions.Invoking(() => FilterFor(new BoundedUploadAttribute()).OnAuthorization(context))
                     .Should().NotThrow();
    }

    /// <summary>
    /// A body already being read cannot have its ceiling moved, and the framework marks that by
    /// making the feature read-only. Writing anyway throws, so the filter has to ask first.
    /// </summary>
    [Fact]
    public void AReadOnlyBodySizeFeatureIsLeftAlone()
    {
        // Arrange
        FakeMaxRequestBodySizeFeature feature = new() { MaxRequestBodySize = 99, IsReadOnly = true };
        AuthorizationFilterContext context = ContextWith(feature);

        // Act
        FilterFor(new BoundedUploadAttribute(2048)).OnAuthorization(context);

        // Assert
        feature.MaxRequestBodySize.Should().Be(99, "the ceiling is fixed once the body is being read");
    }

    /// <summary>
    /// A signed-out caller has no upload to make: a request that could carry a body is refused before
    /// antiforgery reads it, and its ceiling goes to zero so that the server drops the body rather than
    /// draining it. Both forms, because a declared ceiling is no more a stranger's than the cap is.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(2048L)]
    public void AStrangerIsRefusedWithNoCeiling(long? declared)
    {
        // Arrange
        FakeMaxRequestBodySizeFeature feature = new();
        AuthorizationFilterContext context = ContextWith(feature, signedIn: false);

        // Act
        FilterFor(declared is null ? new BoundedUploadAttribute() : new BoundedUploadAttribute(declared.Value))
            .OnAuthorization(context);

        // Assert
        context.Result.Should().BeOfType<ForbidResult>();
        feature.MaxRequestBodySize.Should().Be(0, "a refused body the server would otherwise drain");
    }

    /// <summary>
    /// The page carrying the attribute can be anonymous itself - the home page is - so a stranger's
    /// <c>GET</c> goes through. It still gets no ceiling: a <c>GET</c> has no body to send.
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public void AStrangerMayStillReadThePage(string method)
    {
        // Arrange
        FakeMaxRequestBodySizeFeature feature = new();
        AuthorizationFilterContext context = ContextWith(feature, signedIn: false, method);

        // Act
        FilterFor(new BoundedUploadAttribute()).OnAuthorization(context);

        // Assert
        context.Result.Should().BeNull();
        feature.MaxRequestBodySize.Should().Be(0);
    }

    /// <summary>
    /// The half that stops the refusal above passing by refusing everybody.
    /// </summary>
    [Fact]
    public void ASignedInUploadIsNotRefused()
    {
        // Arrange
        AuthorizationFilterContext context = ContextWith(new FakeMaxRequestBodySizeFeature());

        // Act
        FilterFor(new BoundedUploadAttribute()).OnAuthorization(context);

        // Assert
        context.Result.Should().BeNull();
    }

    private static IAuthorizationFilter FilterFor(BoundedUploadAttribute attribute)
    {
        ServiceCollection services = new();
        services.AddSingleton<IOptionsMonitor<PrintFileStorageOptions>>(
            TestOptions.Monitor(new PrintFileStorageOptions { MaxUploadBytes = ConfiguredCap }));

        // Not disposed: the filter holds only the options value, and the provider owns nothing else.
        ServiceProvider provider = services.BuildServiceProvider();

        return (IAuthorizationFilter)attribute.CreateInstance(provider);
    }

    private static AuthorizationFilterContext ContextWith(IHttpMaxRequestBodySizeFeature? feature,
                                                          bool signedIn = true,
                                                          string method = "POST")
    {
        DefaultHttpContext http = new();
        http.Request.Method = method;

        if (feature is not null)
        {
            http.Features.Set(feature);
        }

        if (signedIn)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "someone")], "Test"));
        }

        return new AuthorizationFilterContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), []);
    }

    private sealed class FakeMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; set; }

        public long? MaxRequestBodySize { get; set; }
    }
}
