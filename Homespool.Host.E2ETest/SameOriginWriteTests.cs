using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Authorisation;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// <see cref="SameOriginWriteFilter"/> through the real pipeline: the sign-in cookie reaches an
/// API write only when the browser attributes the request to this origin.
/// </summary>
/// <remarks>
/// <para>
/// The signed-in client every other test uses already carries <c>Sec-Fetch-Site: same-origin</c>,
/// so those tests are the proof that the header admits; what is pinned here is the refusal and its
/// two edges. The upload endpoint is the sink because it is the simplest write with no printer in it.
/// </para>
/// <para>
/// The bearer row is the guarantee that matters most to a script author: a token never sends the
/// header and must never be asked to. It is asserted here beside the refusal rather than left to the
/// token tests, so that the two halves of the rule are read together.
/// </para>
/// </remarks>
public sealed class SameOriginWriteTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("sameorigin");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }

    private static StreamContent Gcode()
    {
        return new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("G28 ; home\n")));
    }

    /// <summary>The case it exists for: a cookie, a write, and no word from the browser.</summary>
    [Fact]
    public async Task ACookieWriteWithoutTheHeaderIsRefusedWith403()
    {
        // Arrange
        (_, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "noheader@example.com");
        using HttpClient signedIn = client;
        signedIn.DefaultRequestHeaders.Remove(SameOriginWriteFilter.HeaderName);

        using StreamContent body = Gcode();

        // Act
        using HttpResponseMessage response =
            await signedIn.PutAsync("/api/v1/files/noheader.gcode", body, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.Location.Should().BeNull("a refusal is an answer, not a redirect");

        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// <c>same-site</c> is what a sibling subdomain produces, and what <c>SameSite=Lax</c> on the
    /// cookie lets through. This is the gap the filter closes.
    /// </summary>
    [Fact]
    public async Task ACookieWriteFromASiblingOriginIsRefusedWith403()
    {
        // Arrange
        (_, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "sibling@example.com");
        using HttpClient signedIn = client;
        signedIn.DefaultRequestHeaders.Remove(SameOriginWriteFilter.HeaderName);
        signedIn.DefaultRequestHeaders.Add(SameOriginWriteFilter.HeaderName, "same-site");

        using StreamContent body = Gcode();

        // Act
        using HttpResponseMessage response =
            await signedIn.PutAsync("/api/v1/files/sibling.gcode", body, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>A read is never refused: the cookie without the header still lists.</summary>
    [Fact]
    public async Task ACookieReadWithoutTheHeaderIsAdmitted()
    {
        // Arrange
        (_, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "reader@example.com");
        using HttpClient signedIn = client;
        signedIn.DefaultRequestHeaders.Remove(SameOriginWriteFilter.HeaderName);

        // Act
        using HttpResponseMessage response =
            await signedIn.GetAsync("/api/v1/files", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>A bearer token never sends the header and is never asked to.</summary>
    [Fact]
    public async Task ATokenWriteWithoutTheHeaderIsAdmitted()
    {
        // Arrange
        (HSUser user, HttpClient cookieClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "tokenholder@example.com");
        cookieClient.Dispose();

        string plaintext;

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            ApiTokenService tokens = scope.ServiceProvider.GetRequiredService<ApiTokenService>();
            (_, plaintext) = await tokens.CreateAsync(user.Id, "e2e", CapabilitySet.Everything, CancellationToken.None);
        }

        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintext);

        using StreamContent body = Gcode();

        // Act
        using HttpResponseMessage response =
            await client.PutAsync("/api/v1/files/token.gcode", body, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
