using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Services;

namespace Homespool.Host.E2ETest;

/// <summary>
/// What the generated OpenAPI document actually says - as opposed to what the attributes on the
/// controllers say, which is not the same question.
/// </summary>
/// <remarks>
/// <b>This exists because the difference bit us.</b> A <c>[ProducesResponseType]</c> with no type
/// does not document "no body": for a client-error code the generator substitutes
/// <c>ProblemDetails</c> from <c>ApiBehaviorOptions</c>. Documenting the status codes therefore made
/// the document assert a shape the anonymous-object bodies did not have, and every reflection test
/// over the attributes passed while it was wrong - because the attributes were fine and the
/// generator's interpretation of them was the surprise. Only reading the document found it, so
/// reading the document is now a test.
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class OpenApiDocumentTests : IAsyncLifetime, IDisposable
{
    private const string StoragePath = "/api/v1/printers/{uuid}/storage/usb/{path}";

    private static JsonElement SchemaOf(JsonElement operation, string statusCode)
    {
        JsonElement content = operation.GetProperty("responses").GetProperty(statusCode).GetProperty("content");

        foreach (JsonProperty mediaType in content.EnumerateObject())
        {
            return mediaType.Value.GetProperty("schema");
        }

        throw new InvalidOperationException($"{statusCode} is documented with no content at all.");
    }

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-openapi-{Guid.NewGuid():N}.db");
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

    private async Task<JsonElement> DocumentAsync()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the document is served at /openapi/v1.json");

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement
                           .Clone();
    }

    /// <summary>
    /// The success body is described by its own schema - the whole point of naming
    /// <c>Ok&lt;PrinterStorageReadDTO&gt;</c> as an arm of the action's <c>Results&lt;...&gt;</c>
    /// rather than returning <c>IActionResult</c>.
    /// </summary>
    [Fact]
    public async Task TheStorageListingDocumentsItsResponseSchema()
    {
        // Arrange
        JsonElement document = await DocumentAsync();

        // Act
        JsonElement get = document.GetProperty("paths").GetProperty(StoragePath).GetProperty("get");

        // Assert
        SchemaOf(get, "200").GetProperty("$ref").GetString()
                            .Should().Be("#/components/schemas/PrinterStorageReadDTO");

        // And the entry shape it nests, since a listing with untyped children would document nothing
        // that matters.
        document.GetProperty("components").GetProperty("schemas")
                .TryGetProperty("PrinterStorageEntryDTO", out _).Should().BeTrue();
    }

    /// <summary>
    /// Every failure the document describes is a <c>ProblemDetails</c>, as <c>application/problem+json</c>,
    /// and the responses really are - including the 502, which is <b>not</b> a client-error code and
    /// therefore gets no shape inferred for it. That one is the tell: before the types named the shape,
    /// it was the only response the document left honestly undescribed while the 4xx ones claimed a
    /// shape they did not have. Now every failure arm is a <c>ProblemResult</c>, and says both.
    /// </summary>
    [Theory]
    [InlineData("400")]
    [InlineData("401")]
    [InlineData("403")]
    [InlineData("404")]
    [InlineData("409")]
    [InlineData("502")]
    public async Task EveryDocumentedFailureCarriesTheProblemDetailsSchema(string statusCode)
    {
        // Arrange
        JsonElement document = await DocumentAsync();

        // Act
        JsonElement get = document.GetProperty("paths").GetProperty(StoragePath).GetProperty("get");

        // Assert
        SchemaOf(get, statusCode).GetProperty("$ref").GetString()
                                 .Should().Be("#/components/schemas/ProblemDetails");
    }

    /// <summary>
    /// And the content type is the one the arm writes, and only that - where the attribute-era
    /// document listed MVC's three formatter types against a body none of them produced. The 401 is
    /// absent from this list because it is the auth policy's, said once by an attribute at class
    /// level, and an attribute cannot say better than the formatters.
    /// </summary>
    [Theory]
    [InlineData("400")]
    [InlineData("403")]
    [InlineData("404")]
    [InlineData("409")]
    [InlineData("502")]
    public async Task EveryFailureTheActionAnswersIsProblemJson(string statusCode)
    {
        // Arrange
        JsonElement document = await DocumentAsync();

        // Act
        JsonElement get = document.GetProperty("paths").GetProperty(StoragePath).GetProperty("get");
        JsonElement content = get.GetProperty("responses").GetProperty(statusCode).GetProperty("content");

        // Assert
        content.EnumerateObject().Select(mediaType => mediaType.Name)
               .Should().Equal(["application/problem+json"], "the arm says the content type it writes, and only that");
    }

    /// <summary>
    /// The printer-facing protocol is in the document too - including <c>/p/ws</c>, which was absent
    /// until it was given an <c>[HttpGet]</c>: ApiExplorer cannot describe an action with no method
    /// constraint, so a route alone is invisible to it.
    /// </summary>
    [Theory]
    [InlineData("/p/ws", "get")]
    [InlineData("/p/register", "get")]
    [InlineData("/p/register", "post")]
    public async Task ThePrinterProtocolIsDocumented(string path, string verb)
    {
        // Arrange
        JsonElement document = await DocumentAsync();

        // Act
        JsonElement paths = document.GetProperty("paths");

        // Assert
        paths.TryGetProperty(path, out JsonElement operations).Should().BeTrue();
        operations.TryGetProperty(verb, out JsonElement operation).Should().BeTrue();
        operation.GetProperty("responses").EnumerateObject().Should().NotBeEmpty();
    }

    /// <summary>
    /// And its failures are <b>not</b> ProblemDetails, deliberately.
    /// </summary>
    /// <remarks>
    /// <c>ApiExplorerVisibilityConvention</c> records why: <c>[ApiController]</c> is deliberately not
    /// applied to these endpoints, because the status code is the whole contract and a 400 aborts
    /// enrolment once the firmware exhausts its retries. Giving them a body would be a change to
    /// Prusa's protocol surface rather than to ours, so the ProblemDetails rule stops at
    /// <c>/api/v1</c> - and this pins that boundary rather than leaving it to be re-litigated.
    /// </remarks>
    [Fact]
    public async Task ThePrinterProtocolsFailuresCarryNoProblemDetailsBody()
    {
        // Arrange
        JsonElement document = await DocumentAsync();

        // Act
        JsonElement post = document.GetProperty("paths").GetProperty("/p/register").GetProperty("post");
        JsonElement badRequest = post.GetProperty("responses").GetProperty("400");

        // Assert
        badRequest.TryGetProperty("content", out _)
                  .Should().BeFalse("the status code is the whole contract on /p/*");
    }
}
