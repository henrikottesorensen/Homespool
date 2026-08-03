using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The queue endpoints through the real pipeline - routing, cookie authentication, model binding and
/// the service's DI wiring, none of which <c>PrintQueueServiceTests</c> can reach.
/// </summary>
/// <remarks>
/// <para>
/// The ordering and permission rules themselves are tested directly in
/// <c>PrintQueueServiceTests</c>, where the cases are cheap. What is here is the part that breaks
/// silently when a route or a registration moves, plus the two answers a caller must never be able to
/// tell apart: another user's printer and a printer that does not exist.
/// </para>
/// <para>
/// <b>Nothing here talks to a printer</b>, which is why these tests need no connected socket or fake:
/// queueing writes a row, and the producer loop is what would later turn it into a transfer.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class PrintQueueEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-queue-e2e-{Guid.NewGuid():N}.db");
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

    [Fact]
    public async Task AQueuedFileComesBackFromTheListing()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "queuer@example.com");

        Guid uuid = await AddPrinterAsync(user.Id);
        await UploadAsync(client, "benchy.bgcode");

        // Act
        using HttpResponseMessage created = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/queue",
            new { name = "benchy.bgcode" }, TestContext.Current.CancellationToken);

        // Assert
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        using HttpResponseMessage listed = await client.GetAsync($"/api/v1/printers/{uuid}/queue",
            TestContext.Current.CancellationToken);

        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument payload =
            JsonDocument.Parse(await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        JsonElement prints = payload.RootElement.GetProperty("prints");
        prints.GetArrayLength().Should().Be(1);
        prints[0].GetProperty("fileName").GetString().Should().Be("benchy.bgcode");
        prints[0].GetProperty("position").GetInt32().Should().Be(0);

        // The endpoint answers an object rather than a bare array so it can carry this: a client
        // watching a queue that is not moving has the same problem a person does.
        payload.RootElement.TryGetProperty("waiting", out _).Should().BeTrue();

        client.Dispose();
    }

    [Fact]
    public async Task MovingAJobReordersTheListing()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "reorderer@example.com");

        Guid uuid = await AddPrinterAsync(user.Id);
        await UploadAsync(client, "first.bgcode");
        await UploadAsync(client, "second.bgcode");

        await EnqueueAsync(client, uuid, "first.bgcode");
        long secondId = await EnqueueAsync(client, uuid, "second.bgcode");

        // Act
        using HttpResponseMessage moved = await client.PatchAsJsonAsync(
            $"/api/v1/printers/{uuid}/queue/{secondId}", new { position = 0 },
            TestContext.Current.CancellationToken);

        // Assert
        moved.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using JsonDocument payload = await ListAsync(client, uuid);

        JsonElement reordered = payload.RootElement.GetProperty("prints");
        reordered[0].GetProperty("fileName").GetString().Should().Be("second.bgcode");
        reordered[1].GetProperty("fileName").GetString().Should().Be("first.bgcode");

        client.Dispose();
    }

    [Fact]
    public async Task CancellingRemovesAJobFromTheListing()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "canceller@example.com");

        Guid uuid = await AddPrinterAsync(user.Id);
        await UploadAsync(client, "doomed.bgcode");
        long id = await EnqueueAsync(client, uuid, "doomed.bgcode");

        // Act
        using HttpResponseMessage cancelled = await client.DeleteAsync($"/api/v1/printers/{uuid}/queue/{id}",
            TestContext.Current.CancellationToken);

        // Assert
        cancelled.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using JsonDocument payload = await ListAsync(client, uuid);
        payload.RootElement.GetProperty("prints").GetArrayLength().Should().Be(0);

        client.Dispose();
    }

    /// <summary>
    /// Deleting a queued file is refused, so that tidying up files cannot silently cancel a print -
    /// and the queue is shared, so the print may not even be the deleter's own.
    /// </summary>
    [Fact]
    public async Task DeletingAQueuedFileIsRefusedUntilTheJobIsCancelled()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "deleter@example.com");

        Guid uuid = await AddPrinterAsync(user.Id);
        await UploadAsync(client, "wanted.bgcode");
        long id = await EnqueueAsync(client, uuid, "wanted.bgcode");

        // Act
        using HttpResponseMessage refused = await client.DeleteAsync("/api/v1/files/wanted.bgcode",
            TestContext.Current.CancellationToken);

        // Assert
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // And once the job is gone, so may the file be.
        using HttpResponseMessage cancelled = await client.DeleteAsync($"/api/v1/printers/{uuid}/queue/{id}",
            TestContext.Current.CancellationToken);
        cancelled.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage deleted = await client.DeleteAsync("/api/v1/files/wanted.bgcode",
            TestContext.Current.CancellationToken);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        client.Dispose();
    }

    /// <summary>
    /// Someone else's printer and a printer that does not exist have to be the same answer, or a 404
    /// becomes a way to enumerate other people's machines.
    /// </summary>
    [Fact]
    public async Task AnotherUsersPrinterIsIndistinguishableFromOneThatDoesNotExist()
    {
        // Arrange
        (HSUser alice, HttpClient aliceClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "alice-queue@example.com");
        (HSUser _, HttpClient bobClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "bob-queue@example.com");

        Guid alices = await AddPrinterAsync(alice.Id);

        // Act
        using HttpResponseMessage hers = await bobClient.GetAsync($"/api/v1/printers/{alices}/queue",
            TestContext.Current.CancellationToken);
        using HttpResponseMessage imaginary = await bobClient.GetAsync($"/api/v1/printers/{Guid.NewGuid()}/queue",
            TestContext.Current.CancellationToken);

        // Assert
        hers.StatusCode.Should().Be(HttpStatusCode.NotFound);
        imaginary.StatusCode.Should().Be(hers.StatusCode);

        aliceClient.Dispose();
        bobClient.Dispose();
    }

    /// <summary>
    /// The file half of the same rule: names are resolved against the caller's own files, so another
    /// user's file cannot be queued and its existence is not confirmed either.
    /// </summary>
    [Fact]
    public async Task AFileTheCallerDoesNotHaveCannotBeQueued()
    {
        // Arrange
        (HSUser alice, HttpClient aliceClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "alice-files@example.com");

        Guid uuid = await AddPrinterAsync(alice.Id);
        await UploadAsync(aliceClient, "hers.bgcode");

        // Act - Alice's own printer, but a name only she has... asked for by nobody who has it.
        using HttpResponseMessage missing = await aliceClient.PostAsJsonAsync($"/api/v1/printers/{uuid}/queue",
            new { name = "not-mine.bgcode" }, TestContext.Current.CancellationToken);

        // Assert
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        aliceClient.Dispose();
    }

    [Fact]
    public async Task AnAnonymousCallerIsChallengedRatherThanAnswered()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // Act
        using HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/printers/{Guid.NewGuid()}/queue", TestContext.Current.CancellationToken);

        // Assert - 401, not a login redirect: /api answers status codes (notes/api-tokens.md).
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task UploadAsync(HttpClient client, string name)
    {
        using StreamContent body = new(new MemoryStream(Encoding.UTF8.GetBytes("G28 ; home\n")));

        using HttpResponseMessage response =
            await client.PutAsync($"/api/v1/files/{name}", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<long> EnqueueAsync(HttpClient client, Guid uuid, string name)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/queue",
            new { name }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using JsonDocument payload =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return payload.RootElement.GetProperty("id").GetInt64();
    }

    private static async Task<JsonDocument> ListAsync(HttpClient client, Guid uuid)
    {
        using HttpResponseMessage response =
            await client.GetAsync($"/api/v1/printers/{uuid}/queue", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A printer on the user's own default team, inserted directly - enrolling one properly
    /// is <c>EndToEndEnrolmentTests</c>' subject, not this file's.</summary>
    private async Task<Guid> AddPrinterAsync(long userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HSDbContext context = scope.ServiceProvider.GetRequiredService<HSDbContext>();

        TeamMember membership = await context.TeamMembers
            .SingleAsync(member => member.UserId == userId && member.IsDefault, TestContext.Current.CancellationToken);

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
