using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
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
/// What the printer page shows a member who may see the printer but not its queue or its prints,
/// through the real page and its markup.
/// </summary>
/// <remarks>
/// The queue and history services refuse such a reader by throwing, so a page that asked them
/// anyway answered a 500 for the whole printer. The page now reads only what the reader may see, and
/// leaves the queue card and the history disclosure out rather than rendering them empty.
/// </remarks>
public sealed class PrinterDetailCapabilityTests : IAsyncLifetime
{
    private const string QueuedFile = "waiting-cube.gcode";
    private const string FinishedFile = "finished-cylinder.bgcode";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("detail-capability");

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
    /// A member holding <see cref="Capability.ViewPrinter"/> alone gets the page without the queue's
    /// entries, its poll, or the prints - and a Viewer on the same printer, the control, gets all three.
    /// </summary>
    [Fact]
    public async Task AMemberWhoMaySeeOnlyThePrinterIsShownNeitherItsQueueNorItsPrints()
    {
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "detail-capability-owner@example.com");
        (HSUser onlook, HttpClient onlookClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "detail-capability-onlooker@example.com");
        (HSUser viewer, HttpClient viewerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "detail-capability-viewer@example.com");

        using (ownerClient)
        using (onlookClient)
        using (viewerClient)
        {
            (Guid uuid, int teamId) = await SeedAsync(owner.Id);
            await JoinAsync(onlook.Id, teamId, [Capability.ViewPrinter]);
            await JoinAsync(viewer.Id, teamId, CapabilityPresets.Viewer);

            string onlooking = await GetAsync(onlookClient, $"/Printers/Detail/{uuid}");
            string viewing = await GetAsync(viewerClient, $"/Printers/Detail/{uuid}");

            onlooking.Should().Contain("Garage MK4", "the printer itself is theirs to see");
            onlooking.Should().NotContain(QueuedFile, "neither the queue nor what it waits on is theirs to see");
            onlooking.Should().NotContain("handler=Queue", "a poll that would be refused is not started");
            onlooking.Should().NotContain(FinishedFile);
            onlooking.Should().NotContain("Print history");

            viewing.Should().Contain(QueuedFile);
            viewing.Should().Contain("handler=Queue");
            viewing.Should().Contain(FinishedFile);
            viewing.Should().Contain("Print history");
        }
    }

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A printer on the owner's team, one of their files queued on it, and one finished print.</summary>
    private async Task<(Guid uuid, int teamId)> SeedAsync(long ownerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await context.TeamMembers
                                             .SingleAsync(m => m.UserId == ownerId, TestContext.Current.CancellationToken);

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            TeamId = membership.TeamId,
            Name = "Garage MK4",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        HSFile file = new()
        {
            Type = FileType.GCode,
            UserId = ownerId,
            Name = QueuedFile,
            Size = 1024,
            UploadedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        context.Files.Add(file);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.QueuedPrints.Add(new QueuedPrint
        {
            PrinterId = printer.Id,
            FileId = file.Id,
            PrintUuid = Guid.NewGuid(),
            QueuedByUserId = ownerId,
            QueuedByScope = CapabilitySet.Format(CapabilitySet.Everything),
            QueuedAt = DateTimeOffset.UtcNow,
        });
        context.PrintJobs.Add(new PrintJob
        {
            PrinterId = printer.Id,
            FileName = FinishedFile,
            State = PrintState.Finished,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
            CommandedAt = DateTimeOffset.UtcNow.AddHours(-2),
            EndedAt = DateTimeOffset.UtcNow.AddHours(-1),
            QueuedByUserId = ownerId,
            PrintUuid = Guid.NewGuid(),
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (printer.Uuid, printer.TeamId);
    }

    private async Task JoinAsync(long userId, int teamId, IReadOnlyList<Capability> capabilities)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        context.TeamMembers.Add(new TeamMember
        {
            TeamId = teamId,
            UserId = userId,
            Capabilities = CapabilitySet.Format(capabilities),
            IsDefault = false,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
