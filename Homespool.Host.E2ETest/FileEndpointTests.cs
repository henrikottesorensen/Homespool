using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The file endpoints through the real pipeline: routing, authentication by cookie <b>and</b> by
/// personal access token, model binding and the store's DI wiring, none of which a unit test of
/// <c>UserFileStore</c> can reach.
/// </summary>
/// <remarks>
/// <para>
/// The store's own rules - traversal, the extension allowlist, case folding, the size cap - are
/// tested directly in <c>UserFileStoreTests</c> and <c>LengthLimitingStreamTests</c>, where the cases
/// are cheap and exhaustive. These are here to prove the endpoints are reachable and actually reach
/// the store, which is the part that silently breaks when a registration or a route changes - and,
/// for the ownership cases, that the caller's identity is what scopes the answer.
/// </para>
/// <para>
/// <b>The routes are ours as of 2026-07-31</b> (<c>notes/file-storage.md</c>): files are addressed by
/// name rather than by a Connect-shaped hash, and the two start operations became send-versus-print.
/// The tests for <c>printNow</c> and for the <c>hash</c>-shaped upload response went with those
/// shapes rather than being rewritten.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class FileEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-upload-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        // Past the first-run gate, which otherwise redirects everything navigable to /setup. The same
        // call Setup.cshtml.cs makes, for the same reason the other end-to-end suites make it: walking
        // the setup page here would test something SetupGateMiddlewareTests already covers.
        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
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
    public async Task AnUploadedFileComesBackWithItsNameAndSize()
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
        payload.RootElement.GetProperty("name").GetString().Should().Be("model.bgcode",
            "the name is the identity now - there is no hash to report");
        payload.RootElement.GetProperty("size").GetInt64().Should().Be(content.Length);

        client.Dispose();
    }

    /// <summary>
    /// The upload reports where the file will land on the printer, which is what a print call needs
    /// afterwards. Before this the server owned that convention and never stated it, leaving every
    /// caller to reconstruct <c>/usb/</c> + the name and to break silently if it ever changed.
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

        // And the body really is the ProblemDetails the OpenAPI document promises. Asserted on a live
        // response rather than on the attribute, because the attribute was the thing that turned out
        // to be describing a shape the endpoint did not return - see OpenApiDocumentTests.
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        JsonElement problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        problem.GetProperty("status").GetInt32().Should().Be(400);
        problem.GetProperty("detail").GetString().Should().Contain(".gcode");
        problem.TryGetProperty("title", out _).Should().BeTrue();

        client.Dispose();
    }

    /// <summary>
    /// Uploading over an existing name is refused unless it is asked for. The whole reason overwrite
    /// is opt-in: a re-slice and an accident look identical from here, and only one should be silent.
    /// </summary>
    [Fact]
    public async Task AnExistingNameIsA409UntilOverwriteIsAskedFor()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "overwriter@example.com");

        using (StreamContent first = new(new MemoryStream(Encoding.UTF8.GetBytes("first"))))
        {
            (await client.PutAsync("/api/v1/files/benchy.gcode", first)).Dispose();
        }

        // Act
        using StreamContent second = new(new MemoryStream(Encoding.UTF8.GetBytes("second")));
        using HttpResponseMessage conflict = await client.PutAsync("/api/v1/files/benchy.gcode", second);

        using StreamContent third = new(new MemoryStream(Encoding.UTF8.GetBytes("second")));
        using HttpResponseMessage replaced = await client.PutAsync(
            "/api/v1/files/benchy.gcode?overwrite=true", third);

        // Assert
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        replaced.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage download = await client.GetAsync("/api/v1/files/benchy.gcode");
        (await download.Content.ReadAsStringAsync()).Should().Be("second");

        client.Dispose();
    }

    /// <summary>
    /// List, download, rename and delete over one file - the lifecycle the store gained and the API
    /// had no way to reach before.
    /// </summary>
    [Fact]
    public async Task AFileCanBeListedDownloadedRenamedAndDeleted()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "lifecycle@example.com");

        using (StreamContent body = new(new MemoryStream(Encoding.UTF8.GetBytes("G28 ; home\n"))))
        {
            (await client.PutAsync("/api/v1/files/old.gcode", body)).Dispose();
        }

        // Act & Assert
        using (HttpResponseMessage listed = await client.GetAsync("/api/v1/files"))
        {
            using JsonDocument payload = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
            payload.RootElement.GetArrayLength().Should().Be(1);
            payload.RootElement[0].GetProperty("name").GetString().Should().Be("old.gcode");
        }

        using (HttpResponseMessage downloaded = await client.GetAsync("/api/v1/files/old.gcode"))
        {
            (await downloaded.Content.ReadAsStringAsync()).Should().Be("G28 ; home\n");
        }

        using (HttpResponseMessage renamed = await client.PatchAsJsonAsync(
            "/api/v1/files/old.gcode", new { name = "new.gcode" }))
        {
            renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (HttpResponseMessage gone = await client.GetAsync("/api/v1/files/old.gcode"))
        {
            gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        using (HttpResponseMessage moved = await client.GetAsync("/api/v1/files/new.gcode"))
        {
            moved.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (HttpResponseMessage deleted = await client.DeleteAsync("/api/v1/files/new.gcode"))
        {
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using (HttpResponseMessage afterDelete = await client.GetAsync("/api/v1/files/new.gcode"))
        {
            afterDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        client.Dispose();
    }

    /// <summary>
    /// The property the per-user layout exists for, asserted through the pipeline rather than against
    /// the store: one user cannot see, fetch or delete another's file, and "someone else's" is
    /// indistinguishable from "no such file".
    /// </summary>
    [Fact]
    public async Task OneUsersFileIsUnreachableByAnother()
    {
        // Arrange
        (HSUser _, HttpClient alice) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "alice@example.com");
        (HSUser _, HttpClient bob) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "bob@example.com");

        using (StreamContent body = new(new MemoryStream(Encoding.UTF8.GetBytes("G28 ; alice's\n"))))
        {
            (await alice.PutAsync("/api/v1/files/secret.gcode", body)).Dispose();
        }

        // Act & Assert
        using (HttpResponseMessage listed = await bob.GetAsync("/api/v1/files"))
        {
            using JsonDocument payload = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
            payload.RootElement.GetArrayLength().Should().Be(0, "the listing is scoped to the caller");
        }

        using (HttpResponseMessage fetched = await bob.GetAsync("/api/v1/files/secret.gcode"))
        {
            fetched.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "404 rather than 403 - telling them apart would confirm the file exists");
        }

        using (HttpResponseMessage deleted = await bob.DeleteAsync("/api/v1/files/secret.gcode"))
        {
            deleted.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        using (HttpResponseMessage stillThere = await alice.GetAsync("/api/v1/files/secret.gcode"))
        {
            stillThere.StatusCode.Should().Be(HttpStatusCode.OK, "and none of that touched the file");
        }

        alice.Dispose();
        bob.Dispose();
    }

    /// <summary>
    /// Sending refuses a file the caller does not have, with the same answer it gives for one that
    /// does not exist. This is the ownership check on the send path, which the store cannot make on
    /// its own because it never sees who is asking.
    /// </summary>
    [Fact]
    public async Task SendingAFileYouDoNotOwnIsNotFound()
    {
        // Arrange
        (HSUser _, HttpClient alice) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "sender@example.com");
        (HSUser _, HttpClient bob) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "notsender@example.com");

        using (StreamContent body = new(new MemoryStream(Encoding.UTF8.GetBytes("G28\n"))))
        {
            (await alice.PutAsync("/api/v1/files/mine.gcode", body)).Dispose();
        }

        // Act
        using HttpResponseMessage response = await bob.PostAsJsonAsync(
            $"/api/v1/printers/{Guid.NewGuid()}/files", new { name = "mine.gcode" });

        // Assert
        // The file is resolved before the printer is, so this is the file's answer - and it is the
        // same one an unknown name gets.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        alice.Dispose();
        bob.Dispose();
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

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/printers/{Guid.NewGuid()}/print", new { path = "/etc/passwd" });

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
        payload.RootElement.GetProperty("name").GetString().Should().Be("bearer.bgcode");
        payload.RootElement.GetProperty("size").GetInt64().Should().Be(content.Length);
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
