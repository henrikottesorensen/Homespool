using System;
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
/// How long a print took and what it used, as the Detail page's history table says it.
/// </summary>
/// <remarks>
/// The point of the duration column is the split: without the warm-up beside it, a print's wall
/// clock reads as the slicer's estimate being wrong, when minutes of it were homing, probing and
/// heating. Rendered through the real page, so the resources and the column rules are what is
/// asserted rather than a formatter on its own.
/// </remarks>
public sealed class PrintHistoryTableTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("history-table");

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
    /// The measured MK3.5 print: 16 minutes on the wall clock, 3 of them before the first plastic,
    /// and half a metre of filament.
    /// </summary>
    [Fact]
    public async Task APrintSaysHowLongItTookHowMuchWasWarmUpAndWhatItUsed()
    {
        (Guid uuid, HttpClient client) = await SeedAsync(
            "history-warmup@example.com",
            job =>
            {
                job.BegunAt = job.StartedAt.AddSeconds(168);
                job.EndedAt = job.StartedAt.AddSeconds(966);
                job.FilamentAtStart = 1013555.875f;
                job.FilamentAtEnd = 1014055.875f;
            });

        using (client)
        {
            string page = await GetAsync(client, $"/Printers/Detail/{uuid}");

            page.Should().Contain("16 minutes (3 minutes warm-up)");
            page.Should().Contain(">Filament</th>");
            page.Should().Contain("0.50 m");
        }
    }

    /// <summary>
    /// A print whose first plastic was not seen says only its length, and a table where no row has a
    /// filament figure has no column for one.
    /// </summary>
    [Fact]
    public async Task APrintWithoutReadingsSaysOnlyItsLength()
    {
        (Guid uuid, HttpClient client) = await SeedAsync(
            "history-plain@example.com",
            job => job.EndedAt = job.StartedAt.AddMinutes(42));

        using (client)
        {
            string page = await GetAsync(client, $"/Printers/Detail/{uuid}");

            page.Should().Contain("42 minutes");
            page.Should().NotContain("warm-up");
            page.Should().NotContain(">Filament</th>", "no row has a figure to put in it");
        }
    }

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A signed-in member, a printer on their team, and one finished print shaped by <paramref name="shape"/>.</summary>
    private async Task<(Guid uuid, HttpClient client)> SeedAsync(string email, Action<PrintJob> shape)
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, email);

        Guid uuid = Guid.NewGuid();

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await context.TeamMembers
                                             .SingleAsync(m => m.UserId == user.Id, TestContext.Current.CancellationToken);

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

        PrintJob job = new()
        {
            PrinterId = printer.Id,
            FileName = "cylinder.bgcode",
            State = PrintState.Finished,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
            CommandedAt = DateTimeOffset.UtcNow.AddHours(-2),
            QueuedByUserId = user.Id,
            PrintUuid = Guid.NewGuid(),
        };

        shape(job);

        context.PrintJobs.Add(job);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (uuid, client);
    }
}
