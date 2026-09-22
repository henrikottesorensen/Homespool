using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
/// Printing a finished job's file again from the history table.
/// </summary>
/// <remarks>
/// <para>
/// <b>The interesting half is that the file is resolved by name, in the caller's own tree.</b>
/// <see cref="PrintJob.FileName"/> is a record of what ran rather than a pointer at it, so a reprint
/// is a fresh question asked of the file store - and it has to answer "no such file" as readily as it
/// answers "queued", because a history row outlives the file it names.
/// </para>
/// <para>
/// Driven the way a browser drives it, through the form and the antiforgery token, because the button
/// posting the right field name is half of what is being asserted.
/// </para>
/// </remarks>
public sealed class ReprintFromHistoryTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("reprint");

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

    /// <summary>A finished print offers the button, and pressing it puts the file back in the queue.</summary>
    [Fact]
    public async Task PrintingAgainQueuesTheFile()
    {
        (Guid uuid, Guid printUuid, HttpClient client) = await SeedAsync("reprint-ok@example.com", "benchy.bgcode");

        using (client)
        {
            await UploadAsync(client, "benchy.bgcode");

            string page = await GetAsync(client, $"/Printers/Detail/{uuid}");
            page.Should().Contain("handler=Reprint", "the history row carries the button");

            using HttpResponseMessage posted = await PostReprintAsync(client, uuid, page, printUuid);
            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);

            (await QueuedNamesAsync(uuid)).Should().ContainSingle().Which.Should().Be("benchy.bgcode");
        }
    }

    /// <summary>
    /// One enqueue can leave several rows under one handle - a full drive writes a <c>Failed</c> row
    /// while the entry stays queued, and the print that follows writes another - and either row's
    /// button still queues the file once.
    /// </summary>
    [Fact]
    public async Task PrintingAgainAHandleWithSeveralAttemptsQueuesTheFileOnce()
    {
        (Guid uuid, Guid printUuid, HttpClient client) = await SeedAsync("reprint-twice@example.com", "benchy.bgcode");

        using (client)
        {
            await UploadAsync(client, "benchy.bgcode");
            await AddFailedAttemptAsync(uuid, printUuid, "benchy.bgcode");

            string page = await GetAsync(client, $"/Printers/Detail/{uuid}");

            using HttpResponseMessage posted = await PostReprintAsync(client, uuid, page, printUuid);
            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);

            (await QueuedNamesAsync(uuid)).Should().ContainSingle().Which.Should().Be("benchy.bgcode");
        }
    }

    /// <summary>
    /// A file overwritten since the print is asked about rather than printed, and the page's second
    /// button - the same post with <c>changed</c> set - prints the current version.
    /// </summary>
    [Fact]
    public async Task AFileChangedSinceThePrintIsAskedAboutFirst()
    {
        (Guid uuid, Guid printUuid, HttpClient client) = await SeedAsync("reprint-changed@example.com", "benchy.bgcode",
                                                                        digest: "the-bytes-that-printed");

        using (client)
        {
            await UploadAsync(client, "benchy.bgcode");

            string page = await GetAsync(client, $"/Printers/Detail/{uuid}");

            using HttpResponseMessage asked = await PostReprintAsync(client, uuid, page, printUuid);
            asked.StatusCode.Should().Be(HttpStatusCode.Redirect);
            (await QueuedNamesAsync(uuid)).Should().BeEmpty("nothing is queued before the question is answered");

            string question = await GetAsync(client, $"/Printers/Detail/{uuid}");
            question.Should().Contain("name=\"changed\" value=\"true\"", "the page offers to print the current version");

            using HttpResponseMessage answered = await PostReprintAsync(client, uuid, question, printUuid, changed: true);
            answered.StatusCode.Should().Be(HttpStatusCode.Redirect);
            (await QueuedNamesAsync(uuid)).Should().ContainSingle().Which.Should().Be("benchy.bgcode");
        }
    }

    /// <summary>
    /// A history row outlives the file it names, and then the answer is a sentence rather than a
    /// queued print of nothing.
    /// </summary>
    [Fact]
    public async Task PrintingAgainWithoutTheFileRefuses()
    {
        (Guid uuid, Guid printUuid, HttpClient client) = await SeedAsync("reprint-gone@example.com", "deleted.bgcode");

        using (client)
        {
            string page = await GetAsync(client, $"/Printers/Detail/{uuid}");

            using HttpResponseMessage posted = await PostReprintAsync(client, uuid, page, printUuid);
            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);

            (await QueuedNamesAsync(uuid)).Should().BeEmpty("there is no such file to queue");
        }
    }

    /// <summary>
    /// Somebody who may only watch this printer is not offered it - and would be refused if they
    /// posted anyway, which is what the service's own capability check is for.
    /// </summary>
    [Fact]
    public async Task AReadOnlyVisitorIsNotOfferedIt()
    {
        (Guid uuid, Guid _, HttpClient client) = await SeedAsync("reprint-readonly@example.com", "benchy.bgcode",
                                                                 capabilities: CapabilitySet.Format(CapabilityPresets.Viewer));

        using (client)
        {
            string page = await GetAsync(client, $"/Printers/Detail/{uuid}");

            page.Should().NotContain("handler=Reprint");
        }
    }

    /// <summary>
    /// Somebody else's print offers no button, however printable it looks.
    /// </summary>
    /// <remarks>
    /// <b>The hazard is silent, which is why this is not a permission test.</b> The file is found by
    /// name in the caller's own tree, so a button here would offer to print <em>their</em>
    /// <c>benchy.bgcode</c> and queue <em>yours</em> - two different models under one name, printed
    /// without a word said.
    /// </remarks>
    [Fact]
    public async Task SomebodyElsesPrintIsNotOffered()
    {
        (Guid uuid, Guid _, HttpClient client) = await SeedAsync("reprint-theirs@example.com", "benchy.bgcode",
                                                                 queuedBySomebodyElse: true);

        using (client)
        {
            await UploadAsync(client, "benchy.bgcode");

            string page = await GetAsync(client, $"/Printers/Detail/{uuid}");

            page.Should().Contain("benchy.bgcode", "the row is still theirs to read");
            page.Should().NotContain("handler=Reprint");
        }
    }

    /// <summary>
    /// And posting anyway is refused, because a button that is not rendered is not a check - the same
    /// rule every other control on this page states.
    /// </summary>
    [Fact]
    public async Task SomebodyElsesPrintIsRefusedIfPostedAnyway()
    {
        (Guid uuid, Guid printUuid, HttpClient client) = await SeedAsync("reprint-forged@example.com", "benchy.bgcode",
                                                                        queuedBySomebodyElse: true);

        using (client)
        {
            await UploadAsync(client, "benchy.bgcode");

            string page = await GetAsync(client, $"/Printers/Detail/{uuid}");

            using HttpResponseMessage posted = await PostReprintAsync(client, uuid, page, printUuid);
            posted.StatusCode.Should().Be(HttpStatusCode.Redirect);

            (await QueuedNamesAsync(uuid)).Should().BeEmpty("the row belongs to somebody else");
        }
    }

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> PostReprintAsync(HttpClient client,
                                                                    Guid uuid,
                                                                    string page,
                                                                    Guid printUuid,
                                                                    bool changed = false)
    {
        Dictionary<string, string> fields = new()
        {
            ["printUuid"] = printUuid.ToString(),
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page),
        };

        if (changed)
        {
            fields["changed"] = "true";
        }

        using FormUrlEncodedContent body = new(fields);

        return await client.PostAsync($"/Printers/Detail/{uuid}?handler=Reprint", body,
                                      TestContext.Current.CancellationToken);
    }

    private static async Task UploadAsync(HttpClient client, string name)
    {
        using StreamContent body = new(new MemoryStream(Encoding.UTF8.GetBytes(new string('G', 512))));
        using HttpResponseMessage response = await client.PutAsync($"/api/v1/files/{name}", body,
                                                                    TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture upload has to have worked");
    }

    private async Task<IReadOnlyList<string>> QueuedNamesAsync(Guid uuid)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        // QueuedPrint carries no Printer navigation - see PrintJob.PrinterId's own remark on why the
        // telemetry-adjacent tables hold the key alone - so the uuid is resolved first.
        int printerId = await context.Printers
                                     .AsNoTracking()
                                     .Where(p => p.Uuid == uuid)
                                     .Select(p => p.Id)
                                     .SingleAsync(TestContext.Current.CancellationToken);

        return await context.QueuedPrints
                            .AsNoTracking()
                            .Where(q => q.PrinterId == printerId)
                            .Select(q => q.PrintFile!.Name)
                            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A second row under an existing handle, shaped as the queue's full-drive hold writes it: opened
    /// and closed in one moment, before the print that eventually ran.
    /// </summary>
    private async Task AddFailedAttemptAsync(Guid uuid, Guid printUuid, string fileName)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        PrintJob finished = await context.PrintJobs
                                         .AsNoTracking()
                                         .SingleAsync(job => job.PrintUuid == printUuid,
                                                      TestContext.Current.CancellationToken);

        int printerId = await context.Printers
                                     .AsNoTracking()
                                     .Where(p => p.Uuid == uuid)
                                     .Select(p => p.Id)
                                     .SingleAsync(TestContext.Current.CancellationToken);

        DateTimeOffset held = finished.StartedAt.AddMinutes(-30);

        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = printerId,
            FileName = fileName,
            State = PrintState.Failed,
            StartedAt = held,
            EndedAt = held,
            QueuedByUserId = finished.QueuedByUserId,
            PrintUuid = printUuid,
            Reason = "Not enough space on the printer.",
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<(Guid uuid, Guid printUuid, HttpClient client)> SeedAsync(string email,
                                                                                string printedFile,
                                                                                string? capabilities = null,
                                                                                bool queuedBySomebodyElse = false,
                                                                                string? digest = null)
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, email);

        Guid uuid = Guid.NewGuid();

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await context.TeamMembers
                                             .SingleAsync(m => m.UserId == user.Id, TestContext.Current.CancellationToken);

        if (capabilities is not null)
        {
            membership.Capabilities = capabilities;
        }

        Printer printer = new()
        {
            Uuid = uuid,
            TeamId = membership.TeamId,
            Name = "Garage MK3.5",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Guid printUuid = Guid.NewGuid();

        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = printer.Id,
            FileName = printedFile,
            State = PrintState.Finished,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-3),
            EndedAt = DateTimeOffset.UtcNow.AddHours(-1),

            // A user id nobody here holds, which is the whole of what makes a row somebody else's.
            QueuedByUserId = queuedBySomebodyElse ? user.Id + 1000 : user.Id,
            PrintUuid = printUuid,
            Digest = digest,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (uuid, printUuid, client);
    }
}
