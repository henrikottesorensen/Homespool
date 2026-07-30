using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;
using Homespool.Host.Services;
using Homespool.Model.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The upload endpoint through the real pipeline: routing, authentication by cookie <b>and</b> by
/// personal access token, model binding and the store's DI wiring, none of which a unit test of
/// <c>UploadedFileStore</c> can reach.
/// </summary>
/// <remarks>
/// The store's own rules - traversal, the extension allowlist, the size cap - are tested directly in
/// <c>UploadedFileStoreTests</c> and <c>LengthLimitingStreamTests</c>, where the cases are cheap and
/// exhaustive. These are here only to prove the endpoint is reachable and actually reaches the store,
/// which is the part that silently breaks when a registration or a route changes.
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class FileUploadEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-upload-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        // Past the first-run gate, which otherwise redirects everything navigable to /setup. The same
        // call Setup.cshtml.cs makes, for the same reason the other end-to-end suites make it: walking
        // the setup page here would test something SetupGateMiddlewareTests already covers.
        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Dispose();

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _factory.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task AnUploadedFileComesBackWithAHashAndItsLength()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "uploader@example.com");
        byte[] content = Encoding.UTF8.GetBytes("G28 ; home\nG1 X10 Y10\n");

        using StreamContent body = new(new MemoryStream(content));

        // Act
        using HttpResponseMessage response = await client.PutAsync("/api/v1/files/model.bgcode", body);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("fileName").GetString().Should().Be("model.bgcode");
        payload.RootElement.GetProperty("length").GetInt64().Should().Be(content.Length);
        payload.RootElement.GetProperty("hash").GetString().Should().HaveLength(27,
            "the upload's id is also its transfer token, shaped like Connect's own file hashes");

        client.Dispose();
    }

    /// <summary>
    /// The upload reports where the file will land on the printer, which is what
    /// <c>command/start/files</c> needs to print it afterwards. Before this the server owned that
    /// convention and never stated it, leaving every caller to reconstruct <c>/usb/</c> + the name and
    /// to break silently if it ever changed.
    /// </summary>
    /// <remarks>
    /// Asserted as the long name on purpose. It is tempting to expect an 8.3 short name here, because
    /// the printer <i>reports</i> paths that way (<c>is_sfn: true</c>) - but
    /// <c>MarlinPrinter::start_print</c> passes ours to <c>print_begin</c> unconverted
    /// (marlin_printer.cpp:540), and a derived <c>~N</c> index would be a guess that prints a different
    /// file. See <c>notes/protocol-reference.md</c>, "<c>FILE_INFO</c> vs <c>FILE_CHANGED</c>".
    /// </remarks>
    [Fact]
    public async Task AnUploadReportsThePathThePrinterWillKnowItBy()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pathreader@example.com");

        using StreamContent body = new(new MemoryStream(Encoding.UTF8.GetBytes("G28\n")));
        using HttpResponseMessage response = await client.PutAsync("/api/v1/files/a long name.gcode", body);

        using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("printerPath").GetString().Should().Be("/usb/a long name.gcode");

        client.Dispose();
    }

    /// <summary>
    /// The extension check has to run before anything is written - a rejected upload should leave
    /// nothing behind at all.
    /// </summary>
    [Fact]
    public async Task AnUnacceptableExtensionIsRejected()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "uploader2@example.com");

        using StreamContent body = new(new MemoryStream(Encoding.UTF8.GetBytes("not gcode")));

        // Act
        using HttpResponseMessage response = await client.PutAsync("/api/v1/files/payload.txt", body);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        client.Dispose();
    }

    /// <summary>
    /// <c>printNow</c> is in Connect's spec for this operation and is deliberately refused rather
    /// than ignored: printing on completion needs the actor to fire <c>START_PRINT</c> when
    /// <c>TRANSFER_FINISHED</c> arrives, which it does not do yet. Accepting the field and silently
    /// not printing would be the worst of the three options.
    /// </summary>
    [Fact]
    public async Task PrintNowIsRefusedRatherThanIgnored()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "printnow@example.com");

        using StringContent body = new(
            """{"hash":"aaaaaaaaaaaaaaaaaaaaaaaaaaa","teamId":1,"printNow":true}""",
            Encoding.UTF8, "application/json");

        // Act
        using HttpResponseMessage response = await client.PutAsync(
            $"/api/v1/printers/{Guid.NewGuid()}/command/start/cloud", body);

        // Assert
        // Refused before the printer or the file is even looked up - the feature is absent, which is
        // not a fact about this request's arguments.
        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);

        client.Dispose();
    }

    /// <summary>
    /// A path outside <c>/usb/</c> is refused here rather than by the printer. Firmware enforces it
    /// too (<c>path_allowed</c>), but a local rejection explains itself.
    /// </summary>
    [Fact]
    public async Task APathOutsideUsbIsRejected()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "badpath@example.com");

        using StringContent body = new(
            """{"path":"/etc/passwd","printNow":true}""", Encoding.UTF8, "application/json");

        // Act
        using HttpResponseMessage response = await client.PutAsync(
            $"/api/v1/printers/{Guid.NewGuid()}/command/start/files", body);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        client.Dispose();
    }

    /// <summary>
    /// Uploading is not anonymous. The endpoint carries <c>[Authorize]</c>, and an unauthenticated
    /// caller must not be able to write to the server's disk - the reason
    /// <c>notes/internet-exposure.md</c> exists at all.
    /// </summary>
    /// <remarks>
    /// <b>401, exactly</b> - not the login redirect this asserted until personal access tokens landed.
    /// A redirect to an HTML page is useless to a script and arrives as a <c>200</c>, so a caller
    /// checking the status code reads a refusal as success. <c>ApiStatusCodeCookieEvents</c> is what
    /// keeps that answer off <c>/api</c>, and this is the test that would notice it being undone.
    /// </remarks>
    [Fact]
    public async Task AnAnonymousUploadIsRefusedWith401()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using StreamContent body = new(new MemoryStream(Encoding.UTF8.GetBytes("G28")));

        // Act
        using HttpResponseMessage response = await client.PutAsync("/api/v1/files/model.gcode", body);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull("a script has nowhere to follow a redirect to");
        response.Headers.WwwAuthenticate.ToString().Should().Contain("Bearer",
            "the token scheme is in the policy, so its challenge says how to authenticate");
    }

    /// <summary>
    /// The whole point of the feature: a bearer token gets a script through the same endpoint a
    /// browser session reaches by cookie, with no login page, no cookie jar and no antiforgery dance
    /// (<c>notes/api-tokens.md</c>).
    /// </summary>
    [Fact]
    public async Task AnUploadAuthenticatedByBearerTokenSucceeds()
    {
        // Arrange
        (HSUser user, HttpClient cookieClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "tokenholder@example.com");
        cookieClient.Dispose();

        string plaintext;

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            ApiTokenService tokens = scope.ServiceProvider.GetRequiredService<ApiTokenService>();
            (_, plaintext) = await tokens.CreateAsync(user.Id, "e2e", CancellationToken.None);
        }

        // A client with no cookie at all, so nothing but the header can be authenticating this.
        using HttpClient client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintext);

        byte[] content = Encoding.UTF8.GetBytes("G28 ; home\n");
        using StreamContent body = new(new MemoryStream(content));

        // Act
        using HttpResponseMessage response = await client.PutAsync("/api/v1/files/bearer.bgcode", body);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("fileName").GetString().Should().Be("bearer.bgcode");
        payload.RootElement.GetProperty("length").GetInt64().Should().Be(content.Length);
    }

    /// <summary>
    /// A credential of the right shape that was never issued is refused - the case that separates
    /// "authentication happens" from "a header is present".
    /// </summary>
    [Fact]
    public async Task AnUploadWithAnUnissuedBearerTokenIsRefused()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApiTokenService.Prefix + new string('A', ApiTokenService.SecretLength));

        using StreamContent body = new(new MemoryStream(Encoding.UTF8.GetBytes("G28")));

        // Act
        using HttpResponseMessage response = await client.PutAsync("/api/v1/files/model.gcode", body);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
