using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Authentication;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The OctoPrint compatibility surface under <c>/compat/octoprint/{uuid}</c>, driven the way
/// PrusaSlicer drives it: <c>X-Api-Key</c> for every request, a real multipart body, and the two
/// requests a send actually makes.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are end-to-end because every interesting part of this feature is pipeline.</b> The
/// storage rules are `UserFileStoreTests`' and the ordering rules are `PrintQueueServiceTests`' - what
/// is only reachable here is whether the route exists, whether the policy admits the header the
/// slicer sends and refuses the cookie, and whether the body a slicer builds is parsed at all.
/// </para>
/// <para>
/// <b>What this harness cannot settle, stated so nobody mistakes green for proof:</b>
/// <c>WebApplicationFactory</c> runs on <c>TestServer</c>, not Kestrel, so there is no socket, no
/// <c>Expect: 100-continue</c> negotiation and no request-body drain. The open question of whether a
/// 409 refused mid-upload reaches the slicer as <c>HTTP 409</c> or as a connection reset is therefore
/// <em>not</em> answered by <see cref="AnExistingNameIsRefusedWithoutReadingTheFile"/> - that test
/// proves the refusal happens and costs no disk, which is the half a test can prove. The rest needs a
/// real slicer against a real listener.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class OctoPrintCompatEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-octo-e2e-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

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

    // ---------- the version probe ----------

    /// <summary>
    /// The probe answers what the client actually looks for - and, load-bearing, <b>no <c>text</c>
    /// member</b>.
    /// </summary>
    /// <remarks>
    /// That absence is the entire reason the OctoPrint host type was chosen: its validator accepts a
    /// missing <c>text</c> and checks it only when present, where PrusaLink's rejects a missing one and
    /// requires it to name PrusaLink or OctoPrint. Emit a <c>text</c> here and we would be claiming to
    /// be somebody else; emit no <c>api</c> and the slicer reports "Could not parse server response".
    /// </remarks>
    [Fact]
    public async Task TheVersionProbeAnswersWithApiAndNamesNobody()
    {
        // Arrange
        (Guid uuid, string _, HttpClient client) = await SetUpAsync("prober@example.com");

        // Act
        using HttpResponseMessage response = await client.GetAsync($"/compat/octoprint/{uuid}/api/version",
                                                                   TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument payload =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        payload.RootElement.GetProperty("api").GetString().Should().NotBeNullOrWhiteSpace();
        payload.RootElement.TryGetProperty("server", out _).Should().BeTrue();
        payload.RootElement.TryGetProperty("text", out _).Should().BeFalse(
            "a text member would have to name OctoPrint or PrusaLink, and we are neither");

        client.Dispose();
    }

    /// <summary>
    /// A printer this caller cannot see answers 404, so the slicer's Test button tests something real:
    /// "this URL will accept your print", not "something is listening".
    /// </summary>
    [Fact]
    public async Task TheVersionProbe404sForAPrinterTheCallerCannotSee()
    {
        // Arrange
        (Guid _, string _, HttpClient client) = await SetUpAsync("stranger@example.com");

        // Act
        using HttpResponseMessage response = await client.GetAsync(
            $"/compat/octoprint/{Guid.NewGuid()}/api/version", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        client.Dispose();
    }

    // ---------- what the policy admits ----------

    /// <summary>
    /// An unauthenticated probe answers <b>401 with no <c>Location</c></b>.
    /// </summary>
    /// <remarks>
    /// <b>This settles a prerequisite that was recorded and was wrong.</b> The design note claimed
    /// <c>ApiStatusCodeCookieEvents.ApiPathPrefix</c> would have to be widened past <c>/api</c>, or the
    /// cookie handler would answer this with a redirect that the slicer follows to an HTML login page
    /// and reports as "Could not parse server response". That is true only of a surface whose policy
    /// includes the cookie scheme. <c>Policies.Compat</c> names the two token schemes and nothing else,
    /// so the cookie handler is never challenged and cannot redirect. No widening is needed.
    /// </remarks>
    [Fact]
    public async Task AnUnauthenticatedRequestIsRefusedRatherThanRedirected()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        using HttpResponseMessage response = await client.GetAsync(
            $"/compat/octoprint/{Guid.NewGuid()}/api/version", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull("a slicer has nowhere to follow a redirect to");
    }

    /// <summary>
    /// A signed-in browser session is <b>not</b> admitted here, however valid its cookie.
    /// </summary>
    /// <remarks>
    /// The CSRF argument made real: <c>multipart/form-data</c> is a CORS-simple request, so a form on
    /// any site can post one cross-origin, and an <c>[ApiController]</c> carries no antiforgery token.
    /// Admitting the cookie would make the upload route a sink for exactly that. If this test ever goes
    /// green by accident - by someone adding the cookie scheme to <c>Policies.Compat</c> - the
    /// vulnerability is back.
    /// </remarks>
    [Fact]
    public async Task ASignedInCookieIsNotAdmitted()
    {
        // Arrange
        (HSUser user, HttpClient cookieClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "cookieholder@example.com");
        Guid uuid = await AddPrinterAsync(user.Id);

        // Act
        using HttpResponseMessage response = await cookieClient.GetAsync(
            $"/compat/octoprint/{uuid}/api/version", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        cookieClient.Dispose();
    }

    /// <summary>
    /// The slicer's header opens the compatibility surface and <b>nothing else</b>: the same token in
    /// <c>X-Api-Key</c> is refused by <c>/api/v1</c>, which takes it only as a bearer credential.
    /// </summary>
    /// <remarks>
    /// This is the scoping decision made observable, and it was found by accident - three tests here
    /// first verified their work through <c>/api/v1</c> with the slicer's client and got 401. That is
    /// the design behaving, so it is now asserted on purpose. Note what it does <em>not</em> buy: the
    /// holder of this token reaches all of <c>/api/v1</c> by moving it into <c>Authorization</c>. What
    /// it buys is that the unredacted header is not invited anywhere it is not needed.
    /// </remarks>
    [Fact]
    public async Task TheApiKeyHeaderIsRefusedByTheNativeApi()
    {
        // Arrange
        (Guid _, string token, HttpClient client) = await SetUpAsync("scoped@example.com");

        // Act
        using HttpResponseMessage refused = await client.GetAsync("/api/v1/files",
                                                                  TestContext.Current.CancellationToken);

        using HttpClient native = NativeClient(token);
        using HttpResponseMessage allowed = await native.GetAsync("/api/v1/files",
                                                                  TestContext.Current.CancellationToken);

        // Assert
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, "the very same token, in the header /api/v1 takes");

        client.Dispose();
    }

    // ---------- the upload ----------

    /// <summary>
    /// The whole feature: the body PrusaSlicer builds is accepted, the file lands under the caller's
    /// own files, and <c>print=true</c> puts it on that printer's queue.
    /// </summary>
    /// <remarks>
    /// <b><c>print=true</c> queues rather than prints</b>, which is the one place the shell translates
    /// rather than relays. "Upload and Print" becomes upload and queue, because the producer loop owns
    /// starting a print - so the assertion here is a queue row, not a command.
    /// </remarks>
    [Fact]
    public async Task AnUploadWithPrintTrueStoresTheFileAndQueuesIt()
    {
        // Arrange
        (Guid uuid, string token, HttpClient client) = await SetUpAsync("sender@example.com");

        // Act
        using MultipartFormDataContent body = SlicerUpload("benchy.bgcode", print: true);

        using HttpResponseMessage response = await client.PostAsync(
            $"/compat/octoprint/{uuid}/api/files/local", body, TestContext.Current.CancellationToken);

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue(
            "any 2xx is success to the client, which discards the body entirely");

        using HttpClient native = NativeClient(token);

        using HttpResponseMessage listed = await native.GetAsync($"/api/v1/printers/{uuid}/queue",
                                                                 TestContext.Current.CancellationToken);

        using JsonDocument payload =
            JsonDocument.Parse(await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        JsonElement prints = payload.RootElement.GetProperty("prints");
        prints.GetArrayLength().Should().Be(1);
        prints[0].GetProperty("fileName").GetString().Should().Be("benchy.bgcode");

        client.Dispose();
    }

    /// <summary>
    /// Plain <b>Upload</b> stores the file and leaves the queue alone - the distinction the whole
    /// <c>print</c> field exists to make.
    /// </summary>
    [Fact]
    public async Task AnUploadWithPrintFalseQueuesNothing()
    {
        // Arrange
        (Guid uuid, string token, HttpClient client) = await SetUpAsync("uploader@example.com");

        // Act
        using MultipartFormDataContent body = SlicerUpload("shelf.gcode", print: false);

        using HttpResponseMessage response = await client.PostAsync(
            $"/compat/octoprint/{uuid}/api/files/local", body, TestContext.Current.CancellationToken);

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();

        using HttpClient native = NativeClient(token);

        using HttpResponseMessage files = await native.GetAsync("/api/v1/files",
                                                                TestContext.Current.CancellationToken);

        (await files.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("shelf.gcode", "the file is stored either way");

        using HttpResponseMessage listed = await native.GetAsync($"/api/v1/printers/{uuid}/queue",
                                                                 TestContext.Current.CancellationToken);

        using JsonDocument payload =
            JsonDocument.Parse(await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        payload.RootElement.GetProperty("prints").GetArrayLength().Should().Be(0);

        client.Dispose();
    }

    /// <summary>
    /// The <c>path</c> part is discarded, including when it is hostile.
    /// </summary>
    /// <remarks>
    /// The slicer sends the parent directory <em>unescaped</em>, and there are no folders here, so the
    /// only safe treatment is none. The file must land under its plain name regardless of what
    /// <c>path</c> said.
    /// </remarks>
    [Fact]
    public async Task TheUnescapedPathPartIsIgnored()
    {
        // Arrange
        (Guid uuid, string token, HttpClient client) = await SetUpAsync("traverser@example.com");

        // Act
        using MultipartFormDataContent body = SlicerUpload("plain.gcode", print: false, path: "../../../etc");

        using HttpResponseMessage response = await client.PostAsync(
            $"/compat/octoprint/{uuid}/api/files/local", body, TestContext.Current.CancellationToken);

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();

        using HttpClient native = NativeClient(token);

        using HttpResponseMessage stored = await native.GetAsync("/api/v1/files/plain.gcode",
                                                                 TestContext.Current.CancellationToken);

        stored.StatusCode.Should().Be(HttpStatusCode.OK, "the name is the name, whatever path claimed");

        client.Dispose();
    }

    /// <summary>
    /// A name already taken is a 409, the same answer <c>/api/v1</c> gives, and it is reached without
    /// the file's bytes being read.
    /// </summary>
    /// <remarks>
    /// See this class's remarks: the <em>refusal</em> is what is proven here. Whether an early response
    /// mid-upload reaches a real slicer as <c>HTTP 409</c> or as a reset is a socket question this
    /// harness cannot ask. The slicer's send dialog carries an editable filename field, so a clash is
    /// resolvable without leaving the slicer - which is why one rule everywhere beat a silent
    /// overwrite.
    /// </remarks>
    [Fact]
    public async Task AnExistingNameIsRefusedWithoutReadingTheFile()
    {
        // Arrange
        (Guid uuid, string _, HttpClient client) = await SetUpAsync("clasher@example.com");

        using MultipartFormDataContent original = SlicerUpload("same.bgcode", print: false);

        using HttpResponseMessage first = await client.PostAsync(
            $"/compat/octoprint/{uuid}/api/files/local", original, TestContext.Current.CancellationToken);

        first.IsSuccessStatusCode.Should().BeTrue();

        using MultipartFormDataContent resliced = SlicerUpload("same.bgcode", print: false);

        // Act
        using HttpResponseMessage second = await client.PostAsync(
            $"/compat/octoprint/{uuid}/api/files/local", resliced, TestContext.Current.CancellationToken);

        // Assert
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // The body is read by a person, not parsed by a program - see the next test.
        string body = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("same.bgcode").And.Contain("Rename it in the send dialog");

        client.Dispose();
    }

    /// <summary>
    /// A failure body on this surface is <b>plain prose, not <c>ProblemDetails</c></b>, because the
    /// slicer shows it to a person verbatim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>PrintHost::format_error</c> composes <c>"HTTP &lt;status&gt;: &lt;body&gt;"</c> and puts the
    /// result in a dialog. Answering `ProblemDetails` here - correct everywhere else in this
    /// application - showed a real user a JSON document with the one useful sentence between a spec
    /// URL and a trace id. Observed on a live send, 2026-08-06, which is the only way it could have
    /// been: every assertion up to then checked the status code.
    /// </para>
    /// <para>
    /// The brace check is the whole point rather than a proxy for it: no <c>{</c> means no serialised
    /// object of any shape reached a human.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFailureBodyIsProseRatherThanProblemDetails()
    {
        // Arrange
        (Guid uuid, string _, HttpClient client) = await SetUpAsync("reader@example.com");

        using MultipartFormDataContent body = SlicerUpload("notes.txt", print: false);

        // Act
        using HttpResponseMessage response = await client.PostAsync(
            $"/compat/octoprint/{uuid}/api/files/local", body, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be(MediaTypeNames.Text.Plain);

        string text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.Should().NotContain("{", "a serialised object in an error dialog is the defect this guards");
        text.Should().NotContain("traceId", "and a trace id is noise to the person reading it");
        text.Should().Contain(".gcode", "the sentence still has to say what is wrong");

        client.Dispose();
    }

    /// <summary>
    /// A file no printer would accept is refused, and the allowlist is the store's own - reached
    /// through this route rather than reimplemented beside it.
    /// </summary>
    [Fact]
    public async Task AFileThePrinterWouldRefuseIsRefusedHere()
    {
        // Arrange
        (Guid uuid, string _, HttpClient client) = await SetUpAsync("wrongtype@example.com");

        // Act
        using MultipartFormDataContent body = SlicerUpload("notes.txt", print: false);

        using HttpResponseMessage response = await client.PostAsync(
            $"/compat/octoprint/{uuid}/api/files/local", body, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        client.Dispose();
    }

    /// <summary>A body carrying no file part is a 400 rather than a silent success.</summary>
    [Fact]
    public async Task ABodyWithNoFilePartIsRefused()
    {
        // Arrange
        (Guid uuid, string _, HttpClient client) = await SetUpAsync("emptyhanded@example.com");

        using MultipartFormDataContent body = new();
        body.Add(new StringContent("true"), "print");

        // Act
        using HttpResponseMessage response = await client.PostAsync(
            $"/compat/octoprint/{uuid}/api/files/local", body, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        client.Dispose();
    }

    // ---------- the address on the printer's page ----------

    /// <summary>
    /// The printer's page hands over the address to paste into a slicer, ready to use.
    /// </summary>
    /// <remarks>
    /// It is built from the live request rather than from configuration, which is what makes it right
    /// behind a proxy and on a non-standard port. The trailing slash is asserted because the slicer
    /// appends <c>api/version</c> to whatever it is given.
    /// </remarks>
    [Fact]
    public async Task ThePrinterPageOffersTheAddressToPasteIntoASlicer()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pastes@example.com");
        Guid uuid = await AddPrinterAsync(user.Id);

        // Act
        string page = await (await client.GetAsync($"/Printers/Detail/{uuid}", TestContext.Current.CancellationToken))
                            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        page.Should().Contain($"/compat/octoprint/{uuid}/",
                              "the whole point is that nobody has to assemble this by hand");
        page.Should().Contain("OctoPrint", "the host type has to be named or the address is unusable");
        page.Should().Contain("data-copy-button", "and it is copyable, which is why it is here at all");

        client.Dispose();
    }

    /// <summary>
    /// Someone who may read a printer but not change its queue is <b>not</b> offered the address.
    /// </summary>
    /// <remarks>
    /// Not a security boundary - the surface checks for itself, and a determined reader could compose
    /// the URL from the one in their address bar. It is an honesty one: uploading would succeed and
    /// the queue would then refuse, so offering it would be promising something that half works.
    /// </remarks>
    [Fact]
    public async Task AReaderWhoCannotQueueIsNotOfferedTheAddress()
    {
        // Arrange
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "owns@example.com");
        ownerClient.Dispose();

        Guid uuid = await AddPrinterAsync(owner.Id);

        (HSUser reader, HttpClient readerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "reads@example.com");

        await GrantReadOnlyAsync(owner.Id, reader.Id);

        // Act
        string page = await (await readerClient.GetAsync($"/Printers/Detail/{uuid}", TestContext.Current.CancellationToken))
                            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        page.Should().Contain("Print history",
                              "the negative below is worthless unless the page actually rendered for this reader");
        page.Should().NotContain("/compat/octoprint/",
                                 "an address that uploads and then refuses to queue is worse than none");

        readerClient.Dispose();
    }

    /// <summary>
    /// A <c>print</c> part far larger than a flag is refused, rather than read into memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The section needs a bound of its own, which is what this pins.</b> The file part has always
    /// streamed through <c>LengthLimitingStream</c>, while the flag was read with
    /// <c>ReadToEndAsync</c>, which accumulates a whole section into one string however large it is.
    /// The request ceiling alone does not cover that: one form part could spend the entire budget as
    /// managed memory, and unlike a file it never reaches a disk to be bounded there.
    /// </para>
    /// <para>
    /// The answer is 400 rather than 413 because a part this size is not a large flag, it is not a
    /// flag - and 413 here would name the file and the upload limit, neither of which is what went
    /// wrong.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnOversizedPrintPartIsRefusedRatherThanBuffered()
    {
        // Arrange
        (Guid uuid, string _, HttpClient client) = await SetUpAsync("flagstuffer@example.com");

        using MultipartFormDataContent body = new()
        {
            // Three orders of magnitude past a flag, and still well inside the request ceiling, so
            // it is the section's own bound under test rather than the body's.
            { new StringContent(new string('t', 64 * 1024)), "print" },
            { new StringContent(string.Empty), "path" },
        };

        using ByteArrayContent file = new(Encoding.UTF8.GetBytes("G28 ; home\n"));
        body.Add(file, "file", "flagged.gcode");

        // Act
        using HttpResponseMessage response = await client.PostAsync(
            $"/compat/octoprint/{uuid}/api/files/local", body, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        client.Dispose();
    }

    /// <summary>
    /// The refusal above is the bound doing its job rather than the endpoint having stopped taking
    /// uploads. A cap that refused everything would satisfy that test on its own.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryPrintFlagIsStillAccepted()
    {
        // Arrange
        (Guid uuid, string _, HttpClient client) = await SetUpAsync("ordinaryflag@example.com");

        using MultipartFormDataContent body = SlicerUpload("ordinary.gcode", print: false);

        // Act
        using HttpResponseMessage response = await client.PostAsync(
            $"/compat/octoprint/{uuid}/api/files/local", body, TestContext.Current.CancellationToken);

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue("an ordinary flag is nowhere near the cap");

        client.Dispose();
    }

    // ---------- helpers ----------

    /// <summary>
    /// The body PrusaSlicer builds: <c>print</c>, <c>path</c>, and a <c>file</c> part whose part
    /// filename is the target name (<c>docs/prusa-slicer-integration.md</c> §2.3). Part order is the
    /// slicer's own.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification =
                         "Ownership of every part passes to the MultipartFormDataContent returned, which disposes them with itself; each caller disposes that container.")]
    private static MultipartFormDataContent SlicerUpload(string fileName, bool print, string path = "")
    {
        MultipartFormDataContent body = new()
        {
            { new StringContent(print ? "true" : "false"), "print" },
            { new StringContent(path), "path" },
        };

        ByteArrayContent file = new(Encoding.UTF8.GetBytes("G28 ; home\nG1 X10 Y10 F3000\n"));
        body.Add(file, "file", fileName);

        return body;
    }

    /// <summary>
    /// A user with a printer and a token, and a client that carries <b>only</b> <c>X-Api-Key</c> - no
    /// cookie, so nothing but the header can be authenticating anything below.
    /// </summary>
    private async Task<(Guid uuid, string token, HttpClient client)> SetUpAsync(string email)
    {
        (HSUser user, HttpClient cookieClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, email);
        cookieClient.Dispose();

        Guid uuid = await AddPrinterAsync(user.Id);

        string plaintext;

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            ApiTokenService tokens = scope.ServiceProvider.GetRequiredService<ApiTokenService>();
            (_, plaintext) = await tokens.CreateAsync(user.Id, "slicer", CapabilitySet.Everything, CancellationToken.None);
        }

        HttpClient client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(XApiKeyAuthenticationHandler.HeaderName, plaintext);

        return (uuid, plaintext, client);
    }

    /// <summary>
    /// A client carrying the same token the slicer holds, in the header <c>/api/v1</c> accepts. Used
    /// only to <em>verify</em> what an upload did, never to perform one.
    /// </summary>
    private HttpClient NativeClient(string token)
    {
        HttpClient client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    /// <summary>Puts <paramref name="readerId"/> in the owner's default team with reading only.</summary>
    private async Task GrantReadOnlyAsync(long ownerId, long readerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember owner = await context.TeamMembers
                                        .SingleAsync(member => member.UserId == ownerId && member.IsDefault,
                                                     TestContext.Current.CancellationToken);

        context.TeamMembers.Add(new TeamMember
        {
            TeamId = owner.TeamId,
            UserId = readerId,
            Capabilities = CapabilitySet.Format(CapabilityPresets.Viewer),
            IsDefault = false,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> AddPrinterAsync(long userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await context.TeamMembers
                                             .SingleAsync(member => member.UserId == userId && member.IsDefault,
                                                          TestContext.Current.CancellationToken);

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = membership.TeamId,
            Status = PrinterStatus.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return printer.Uuid;
    }
}
