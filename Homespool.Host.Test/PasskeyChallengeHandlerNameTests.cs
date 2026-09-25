using AwesomeAssertions;

using Microsoft.AspNetCore.Http;

using Homespool.Host.Pages.Account;
using Homespool.Host.Pages.Account.Manage;

namespace Homespool.Host.Test;

/// <summary>
/// Which requests <see cref="PasskeyChallengeRateLimit.IsChallenge"/> counts as a challenge: exactly
/// those Razor Pages will send to a challenge handler, however the handler is named.
/// </summary>
/// <remarks>
/// The framework takes the <c>handler</c> route value when there is one, and otherwise the first
/// <c>handler</c> query value. Any other reading limits a request that runs something else, or lets
/// a challenge through unlimited - a repeated parameter read as one string did the second.
/// </remarks>
public sealed class PasskeyChallengeHandlerNameTests
{
    private static HttpContext Post(string query, string? routed = null)
    {
        DefaultHttpContext context = new();

        context.Request.Method = HttpMethods.Post;
        context.Request.QueryString = new QueryString(query);

        if (routed is not null)
        {
            context.Request.RouteValues["handler"] = routed;
        }

        return context;
    }

    [Theory]
    [InlineData("?handler=" + LoginModel.PasskeyOptionsHandler)]
    [InlineData("?handler=" + PasskeysModel.BeginRegistrationHandler)]
    [InlineData("?handler=" + LoginModel.PasskeyOptionsHandler + "&handler=" + LoginModel.PasskeyOptionsHandler)]
    [InlineData("?handler=" + LoginModel.PasskeyOptionsHandler + "&handler=Other")]
    [InlineData("?handler=passkeyoptions")]
    public void TheHandlerThatRunsIsAChallenge(string query)
    {
        PasskeyChallengeRateLimit.IsChallenge(Post(query)).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("?handler=Other")]
    [InlineData("?handler=Other&handler=" + LoginModel.PasskeyOptionsHandler)]
    public void AHandlerThatIsNotAChallengeIsNot(string query)
    {
        PasskeyChallengeRateLimit.IsChallenge(Post(query)).Should().BeFalse("a later value never runs, so it must not be limited as though it did");
    }

    [Fact]
    public void ARouteValueNamesTheHandlerBeforeTheQuery()
    {
        PasskeyChallengeRateLimit.IsChallenge(Post("?handler=Other", routed: LoginModel.PasskeyOptionsHandler)).Should().BeTrue();
        PasskeyChallengeRateLimit.IsChallenge(Post("?handler=" + LoginModel.PasskeyOptionsHandler, routed: "Other")).Should().BeFalse();
    }

    [Fact]
    public void AGetIsNeverAChallenge()
    {
        HttpContext context = Post("?handler=" + LoginModel.PasskeyOptionsHandler);
        context.Request.Method = HttpMethods.Get;

        PasskeyChallengeRateLimit.IsChallenge(context).Should().BeFalse();
    }
}
