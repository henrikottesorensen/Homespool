using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// What the pages reached from an emailed link do with a code that is not one.
/// </summary>
/// <remarks>
/// <para>
/// Every such link carries a base64url-wrapped token, and the decoder throws on anything that is not
/// valid base64url. Three of the five pages that unwrap one called it bare, so an anonymous caller
/// got a 500 - and the ordinary way to arrive there is not an attacker but a mail client that broke
/// a long link across two lines.
/// </para>
/// <para>
/// Driven end to end because the failure was an unhandled exception escaping a handler, which is a
/// property of the pipeline rather than of the method: a unit test calling the page model would see
/// the exception rather than the status somebody actually receives.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class MalformedEmailedCodeTests : IAsyncLifetime, IDisposable
{
    /// <summary>Not valid base64url - '!' is outside the alphabet, so the decoder throws on it.</summary>
    private const string NotACode = "!!!not-base64url!!!";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-badcode-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Fact]
    public async Task ConfirmEmailAnswersRatherThanFaulting()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "badconfirm@example.com");

        using HttpResponseMessage response = await client.GetAsync(
            $"/Account/ConfirmEmail?userId={user.Id}&code={NotACode}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError,
                                           "a link broken in transit is not a server fault");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        client.Dispose();
    }

    [Fact]
    public async Task ConfirmEmailChangeAnswersRatherThanFaulting()
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "badchange@example.com");

        using HttpResponseMessage response = await client.GetAsync(
            $"/Account/ConfirmEmailChange?userId={user.Id}&email=new@example.com&code={NotACode}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        client.Dispose();
    }

    [Fact]
    public async Task ResetPasswordAnswersRatherThanFaulting()
    {
        // Anonymous, which is the point: this one needs no account at all to reach.
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            $"/Account/ResetPassword?code={NotACode}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                                        "the page already answers a missing code this way, and an "
                                        + "unusable one is the same answer to the person holding it");
    }
}
