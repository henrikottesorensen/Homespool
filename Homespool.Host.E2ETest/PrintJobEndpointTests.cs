using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// A printer's prints over the API - the listing and printing one again - through the real pipeline.
/// </summary>
/// <remarks>
/// <para>
/// History rows are inserted directly, shaped as the queue's loop writes them: the loop is what opens
/// and closes them, and driving a print to completion is <c>FakePrinterIntegrationTests</c>' subject.
/// What is here is the routes, the refusals, and the handle that one enqueue can leave on several rows.
/// </para>
/// <para>
/// The enqueue endpoint's warnings are here too, beside the reprint's, because both answer with the
/// same shape and the case that makes them say something is the same.
/// </para>
/// </remarks>
public sealed class PrintJobEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Morning = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("jobs-e2e");
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

    /// <summary>
    /// The running print sits apart from the finished ones, which come newest first, each naming who
    /// queued it by handle and name.
    /// </summary>
    [Fact]
    public async Task TheListingSeparatesTheRunningPrintFromTheFinishedOnes()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "lister@example.com");

        using (client)
        {
            Guid uuid = await AddPrinterAsync(user.Id);
            await AddPrintAsync(uuid, "old.bgcode", user.Id, PrintState.Finished, Morning);
            await AddPrintAsync(uuid, "newer.bgcode", user.Id, PrintState.Stopped, Morning.AddHours(2), stoppedBy: user.Id);
            Guid running = await AddPrintAsync(uuid, "now.bgcode", user.Id, PrintState.Printing, Morning.AddHours(4), ended: false);

            // Act
            using JsonDocument payload = await ListAsync(client, $"/api/v1/printers/{uuid}/jobs");

            // Assert
            JsonElement active = payload.RootElement.GetProperty("active");
            active.GetProperty("printUuid").GetGuid().Should().Be(running);
            active.GetProperty("state").GetString().Should().Be("Printing");
            active.GetProperty("endedAt").ValueKind.Should().Be(JsonValueKind.Null);

            JsonElement prints = payload.RootElement.GetProperty("prints");
            prints.EnumerateArray().Select(print => print.GetProperty("fileName").GetString())
                  .Should().Equal("newer.bgcode", "old.bgcode");

            JsonElement queuedBy = prints[0].GetProperty("queuedBy");
            queuedBy.GetProperty("uuid").GetGuid().Should().Be(user.Uuid);
            queuedBy.GetProperty("userName").GetString().Should().Be(user.UserName);

            prints[0].GetProperty("stoppedBy").GetProperty("uuid").GetGuid().Should().Be(user.Uuid);
            prints[1].GetProperty("stoppedBy").ValueKind.Should().Be(JsonValueKind.Null, "nobody here stopped it");
            prints[0].GetProperty("canReprint").GetBoolean().Should().BeTrue();

            payload.RootElement.ToString().Should().NotContain("\"id\"", "the row's own key never leaves the app");
        }
    }

    /// <summary>
    /// Reading further back is a cursor on the start time, and the running print belongs only to the
    /// first page.
    /// </summary>
    [Fact]
    public async Task ALimitAndACursorPageBackwardsThroughTheFinishedPrints()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pager@example.com");

        using (client)
        {
            Guid uuid = await AddPrinterAsync(user.Id);
            await AddPrintAsync(uuid, "one.bgcode", user.Id, PrintState.Finished, Morning);
            await AddPrintAsync(uuid, "two.bgcode", user.Id, PrintState.Finished, Morning.AddHours(1));
            await AddPrintAsync(uuid, "three.bgcode", user.Id, PrintState.Finished, Morning.AddHours(2));
            await AddPrintAsync(uuid, "now.bgcode", user.Id, PrintState.Printing, Morning.AddHours(3), ended: false);

            // Act
            using JsonDocument first = await ListAsync(client, $"/api/v1/printers/{uuid}/jobs?limit=2");

            JsonElement firstPrints = first.RootElement.GetProperty("prints");
            string? cursor = firstPrints[firstPrints.GetArrayLength() - 1].GetProperty("startedAt").GetString();

            using JsonDocument second = await ListAsync(
                client, $"/api/v1/printers/{uuid}/jobs?limit=2&before={Uri.EscapeDataString(cursor!)}");

            // Assert
            firstPrints.EnumerateArray().Select(print => print.GetProperty("fileName").GetString())
                       .Should().Equal("three.bgcode", "two.bgcode");
            first.RootElement.GetProperty("active").ValueKind.Should().Be(JsonValueKind.Object);

            second.RootElement.GetProperty("prints").EnumerateArray()
                  .Select(print => print.GetProperty("fileName").GetString())
                  .Should().Equal("one.bgcode");
            second.RootElement.GetProperty("active").ValueKind.Should().Be(JsonValueKind.Null,
                "a page further back is about the past");
        }
    }

    /// <summary>
    /// Printing again queues the file under a handle of its own, and answers with the new entry.
    /// </summary>
    [Fact]
    public async Task ReprintingQueuesTheFileAsANewEntry()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "reprinter@example.com");

        using (client)
        {
            Guid uuid = await AddPrinterAsync(user.Id);
            await UploadAsync(client, "benchy.bgcode");
            Guid printed = await AddPrintAsync(uuid, "benchy.bgcode", user.Id, PrintState.Finished, Morning);

            // Act
            using HttpResponseMessage response = await ReprintAsync(client, uuid, printed);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Created);
            response.Headers.Location!.ToString().Should().EndWith($"/api/v1/printers/{uuid}/queue");

            using JsonDocument payload = await ReadAsync(response);
            payload.RootElement.GetProperty("fileName").GetString().Should().Be("benchy.bgcode");
            payload.RootElement.GetProperty("printUuid").GetGuid().Should().NotBe(printed,
                "a new entry is a new intention");
            payload.RootElement.GetProperty("warnings").GetArrayLength().Should().Be(0);

            using JsonDocument queue = await ListAsync(client, $"/api/v1/printers/{uuid}/queue");
            queue.RootElement.GetProperty("prints").GetArrayLength().Should().Be(1);
        }
    }

    /// <summary>
    /// A full drive leaves a failed row under the same handle as the print that followed it, and
    /// either way the file is queued once.
    /// </summary>
    [Fact]
    public async Task ReprintingAHandleWithSeveralAttemptsQueuesTheFileOnce()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "twice@example.com");

        using (client)
        {
            Guid uuid = await AddPrinterAsync(user.Id);
            await UploadAsync(client, "benchy.bgcode");
            Guid printed = await AddPrintAsync(uuid, "benchy.bgcode", user.Id, PrintState.Failed, Morning);
            await AddPrintAsync(uuid, "benchy.bgcode", user.Id, PrintState.Finished, Morning.AddHours(1), printUuid: printed);

            // Act
            using HttpResponseMessage response = await ReprintAsync(client, uuid, printed);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Created);

            using JsonDocument queue = await ListAsync(client, $"/api/v1/printers/{uuid}/queue");
            queue.RootElement.GetProperty("prints").GetArrayLength().Should().Be(1);

            using JsonDocument jobs = await ListAsync(client, $"/api/v1/printers/{uuid}/jobs");
            jobs.RootElement.GetProperty("prints").EnumerateArray()
                .Select(print => print.GetProperty("printUuid").GetGuid())
                .Should().Equal([printed, printed], "both attempts are listed, under the one handle");
        }
    }

    /// <summary>
    /// Somebody else's print is refused, however printable - the file is looked up among the caller's
    /// own, so going ahead would print the caller's file under that name.
    /// </summary>
    [Fact]
    public async Task SomebodyElsesPrintIsRefused()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "not-mine@example.com");

        using (client)
        {
            Guid uuid = await AddPrinterAsync(user.Id);
            await UploadAsync(client, "benchy.bgcode");
            Guid theirs = await AddPrintAsync(uuid, "benchy.bgcode", user.Id + 1000, PrintState.Finished, Morning);

            // Act
            using HttpResponseMessage response = await ReprintAsync(client, uuid, theirs);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            using JsonDocument jobs = await ListAsync(client, $"/api/v1/printers/{uuid}/jobs");
            jobs.RootElement.GetProperty("prints")[0].GetProperty("canReprint").GetBoolean().Should().BeFalse();

            using JsonDocument queue = await ListAsync(client, $"/api/v1/printers/{uuid}/queue");
            queue.RootElement.GetProperty("prints").GetArrayLength().Should().Be(0);
        }
    }

    /// <summary>
    /// The print exists and the file it would print does not - a conflict, not a missing print.
    /// </summary>
    [Fact]
    public async Task ReprintingAFileThatHasGoneIsAConflict()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "gone@example.com");

        using (client)
        {
            Guid uuid = await AddPrinterAsync(user.Id);
            Guid printed = await AddPrintAsync(uuid, "deleted.bgcode", user.Id, PrintState.Finished, Morning);

            // Act
            using HttpResponseMessage response = await ReprintAsync(client, uuid, printed);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
    }

    /// <summary>
    /// No such print, and a print on a printer the caller cannot see, are the same answer.
    /// </summary>
    [Fact]
    public async Task AnUnknownPrintAndAnotherUsersPrinterAreBothNotFound()
    {
        // Arrange
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "owner@example.com");
        ownerClient.Dispose();

        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "stranger@example.com");

        using (client)
        {
            Guid mine = await AddPrinterAsync(user.Id);
            Guid theirs = await AddPrinterAsync(owner.Id);
            Guid theirPrint = await AddPrintAsync(theirs, "benchy.bgcode", owner.Id, PrintState.Finished, Morning);

            // Act
            using HttpResponseMessage unknown = await ReprintAsync(client, mine, Guid.NewGuid());
            using HttpResponseMessage elsewhere = await ReprintAsync(client, theirs, theirPrint);
            using HttpResponseMessage listed = await client.GetAsync($"/api/v1/printers/{theirs}/jobs",
                                                                     TestContext.Current.CancellationToken);

            // Assert
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
            elsewhere.StatusCode.Should().Be(HttpStatusCode.NotFound);
            listed.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// A token lent the right to read history but not to print can list, and cannot reprint - and is
    /// told it cannot print rather than offered a button it cannot press.
    /// </summary>
    [Fact]
    public async Task ATokenThatMayOnlyReadCannotReprint()
    {
        // Arrange
        (HSUser user, HttpClient signedIn) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "reader-token@example.com");

        await UploadAsync(signedIn, "benchy.bgcode");
        signedIn.Dispose();

        Guid uuid = await AddPrinterAsync(user.Id);
        Guid printed = await AddPrintAsync(uuid, "benchy.bgcode", user.Id, PrintState.Finished, Morning);

        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await MintTokenAsync(user.Id, Capability.ViewPrinter, Capability.ViewHistory));

        // Act
        using JsonDocument jobs = await ListAsync(client, $"/api/v1/printers/{uuid}/jobs");
        using HttpResponseMessage response = await ReprintAsync(client, uuid, printed);

        // Assert
        jobs.RootElement.GetProperty("prints")[0].GetProperty("canReprint").GetBoolean().Should().BeFalse();
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Queueing a file made for another printer still queues it, and says the queue will stop there -
    /// through both doors a file reaches a queue by over the API.
    /// </summary>
    [Fact]
    public async Task QueueingAndReprintingBothSayWhenTheQueueWillHold()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "mismatch@example.com");

        using (client)
        {
            Guid uuid = await AddPrinterAsync(user.Id, model: "MK3.5");
            await UploadAsync(client, "coreone.bgcode");
            await MarkMadeForAsync(user.Id, "coreone.bgcode", "COREONE");
            Guid printed = await AddPrintAsync(uuid, "coreone.bgcode", user.Id, PrintState.Finished, Morning);

            // Act
            using HttpResponseMessage queued = await client.PostAsJsonAsync($"/api/v1/printers/{uuid}/queue",
                                                                            new { name = "coreone.bgcode" },
                                                                            TestContext.Current.CancellationToken);
            using HttpResponseMessage reprinted = await ReprintAsync(client, uuid, printed);

            // Assert
            foreach (HttpResponseMessage response in new[] { queued, reprinted })
            {
                response.StatusCode.Should().Be(HttpStatusCode.Created, "a warning is not a refusal");

                using JsonDocument payload = await ReadAsync(response);
                JsonElement warning = payload.RootElement.GetProperty("warnings").EnumerateArray().Single();

                warning.GetProperty("severity").GetString().Should().Be("Hold");
                warning.GetProperty("message").GetString().Should().Contain("coreone.bgcode");
            }
        }
    }

    private static async Task<HttpResponseMessage> ReprintAsync(HttpClient client, Guid printer, Guid print)
    {
        return await client.PostAsync($"/api/v1/printers/{printer}/jobs/{print}/reprint", content: null,
                                      TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> ListAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadAsync(response);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response)
    {
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task UploadAsync(HttpClient client, string name)
    {
        using StreamContent body = new(new MemoryStream(Encoding.UTF8.GetBytes("G28 ; home\n")));

        using HttpResponseMessage response =
            await client.PutAsync($"/api/v1/files/{name}", body, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture upload has to have worked");
    }

    private async Task<string> MintTokenAsync(long userId, params Capability[] scope)
    {
        using IServiceScope services = _factory.Services.CreateScope();

        ApiTokenService tokens = services.ServiceProvider.GetRequiredService<ApiTokenService>();
        (_, string plaintext) = await tokens.CreateAsync(userId, "reader", CapabilitySet.Parse(CapabilitySet.Format(scope)),
                                                         CancellationToken.None);

        return plaintext;
    }

    /// <summary>The file's own account of which printer it was sliced for, as the reader writes it at upload.</summary>
    private async Task MarkMadeForAsync(long userId, string name, string printerModel)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        PrintFile file = await context.PrintFiles.SingleAsync(row => row.UserId == userId && row.Name == name,
                                                              TestContext.Current.CancellationToken);
        file.MetadataState = PrintFileMetadataState.Read;
        file.PrinterModel = printerModel;

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A printer on the user's own default team, inserted directly.</summary>
    private async Task<Guid> AddPrinterAsync(long userId, string? model = null)
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
            Model = model,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return printer.Uuid;
    }

    /// <summary>
    /// One history row, as the queue's loop writes it. <paramref name="printUuid"/> reuses a handle,
    /// which is how one enqueue comes to leave several rows.
    /// </summary>
    private async Task<Guid> AddPrintAsync(Guid printerUuid,
                                           string fileName,
                                           long queuedBy,
                                           PrintState state,
                                           DateTimeOffset startedAt,
                                           bool ended = true,
                                           long? stoppedBy = null,
                                           Guid? printUuid = null)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        int printerId = await context.Printers
                                     .AsNoTracking()
                                     .Where(printer => printer.Uuid == printerUuid)
                                     .Select(printer => printer.Id)
                                     .SingleAsync(TestContext.Current.CancellationToken);

        PrintJob job = new()
        {
            PrinterId = printerId,
            PrintUuid = printUuid ?? Guid.NewGuid(),
            FileName = fileName,
            QueuedByUserId = queuedBy,
            State = state,
            StartedAt = startedAt,
            CommandedAt = startedAt,
            EndedAt = ended ? startedAt.AddMinutes(30) : null,
            StoppedByUserId = stoppedBy,
        };

        context.PrintJobs.Add(job);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return job.PrintUuid;
    }
}
