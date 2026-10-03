using System.Net;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;

using Homespool.Host.Pages.Account;
using Homespool.Host.Pages.Account.Manage;

namespace Homespool.Host.Test;

/// <summary>
/// Which window a request to the Manage page's passkey policy falls in.
/// </summary>
public sealed class PasskeyChallengePartitionTests
{
    private const string NoLimiter = "";

    private static HttpContext Post(string query, string address)
    {
        DefaultHttpContext context = new();

        context.Request.Method = HttpMethods.Post;
        context.Request.QueryString = new QueryString(query);
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);

        return context;
    }

    [Fact]
    public void AChallengeIsLimitedPerAddress()
    {
        HttpContext context = Post("?handler=" + PasskeysModel.BeginRegistrationHandler, "203.0.113.7");

        PasskeyChallengeRateLimit.Partition(context).PartitionKey.Should().Be("203.0.113.7");
    }

    /// <summary>An IPv6 client is its /64, so rotating its address inside it buys no new window.</summary>
    [Fact]
    public void AnIPv6ChallengeIsLimitedPerSlash64()
    {
        HttpContext context = Post("?handler=" + PasskeysModel.BeginRegistrationHandler, "2001:db8:1:2::abcd");

        PasskeyChallengeRateLimit.Partition(context).PartitionKey.Should().Be("2001:db8:1:2::/64");
    }

    [Fact]
    public void AnythingElseOnThePageIsNotLimited()
    {
        PasskeyChallengeRateLimit.Partition(Post("?handler=Other", "203.0.113.7")).PartitionKey.Should().Be(NoLimiter);
    }
}
