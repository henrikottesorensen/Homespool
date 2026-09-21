using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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
/// A printer's telemetry over the API - its state now, and its temperatures over time.
/// </summary>
/// <remarks>
/// <para>
/// Live state and samples are inserted directly, shaped as the telemetry writer leaves them; the path
/// from the wire into those rows is <c>FakePrinterIntegrationTests</c>' subject. No printer is
/// connected in any of these, so <c>connected</c> is false throughout - which is the case a script has
/// to be able to recognise, since the numbers look the same either way.
/// </para>
/// <para>
/// <b>Several assertions read the raw body rather than a parsed number</b>, because the defect they
/// guard is in the text: a float widened to a double parses back to the same value and prints as
/// <c>0.40000000596046448</c>.
/// </para>
/// </remarks>
public sealed class PrinterTelemetryEndpointTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("telemetry-e2e");
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
    /// A single-tool printer mid-print: its heaters, its job, and its one tool, synthesised from the
    /// flat fields because firmware sends no slot block for a single head.
    /// </summary>
    [Fact]
    public async Task APrintingPrinterAnswersItsHeatersItsJobAndItsTool()
    {
        // Arrange
        (Guid uuid, int printerId, HttpClient client) = await SeedAsync("printing@example.com");

        using (client)
        {
            await AddLiveStateAsync(new PrinterLiveState
            {
                PrinterId = printerId,
                LastSeenAt = DateTimeOffset.UtcNow,
                Status = PrinterStatus.Printing,
                JobId = 7,
                Progress = 42,
                TimePrinting = 3600,
                TimeRemaining = 4800,
                NozzleTemperature = 214.8f,
                TargetNozzleTemperature = 215,
                BedTemperature = 59.9f,
                TargetBedTemperature = 60,
                Material = "PETG",
                Speed = 100,
                Flow = 95,
                ExtruderFan = 3000,
                PrintFan = 5200,
                FilamentUsed = 1013555.875f,
            });
            await AddToolAsync(printerId, 1, nozzleDiameter: 0.4f);

            // Act
            (JsonElement body, string raw) = await GetAsync(client, $"/api/v1/printers/{uuid}/telemetry");

            // Assert
            body.GetProperty("connected").GetBoolean().Should().BeFalse("no printer is connected in this test");
            body.GetProperty("state").GetString().Should().Be("PRINTING");
            body.GetProperty("temperatures").GetProperty("nozzle").GetProperty("target").GetSingle().Should().Be(215);
            body.GetProperty("job").GetProperty("progress").GetInt32().Should().Be(42);
            body.GetProperty("filamentUsed").GetSingle().Should().Be(1013555.875f,
                "the odometer is the printer's, not the job's");
            body.GetProperty("attention").ValueKind.Should().Be(JsonValueKind.Null);

            JsonElement tool = body.GetProperty("tools").EnumerateArray().Single();
            tool.GetProperty("toolNumber").GetInt32().Should().Be(1);
            tool.GetProperty("material").GetString().Should().Be("PETG");
            tool.GetProperty("picked").GetBoolean().Should().BeFalse("a single head is never 'picked'");

            raw.Should().Contain("\"current\":214.8,").And.Contain("\"nozzleDiameter\":0.4,",
                "a float must not print its double widening");
        }
    }

    /// <summary>
    /// A toolchanger's heads come from its slot block, numbered as the printer numbers them - with
    /// gaps - and the one on the carriage is marked.
    /// </summary>
    [Fact]
    public async Task AToolchangerAnswersEveryHeadByItsOwnNumber()
    {
        // Arrange
        (Guid uuid, int printerId, HttpClient client) = await SeedAsync("toolchanger@example.com");

        using (client)
        {
            PrinterLiveState state = new()
            {
                PrinterId = printerId,
                LastSeenAt = DateTimeOffset.UtcNow,
                Status = PrinterStatus.Idle,
                ActiveSlot = 5,
            };

            foreach (int slot in new[] { 1, 2, 5 })
            {
                state.Slots.Add(new PrinterLiveSlotState
                {
                    PrinterId = printerId,
                    SlotNumber = slot,
                    Material = slot == 2 ? null : "PLA",
                    Temperature = 20 + slot,
                });
            }

            await AddLiveStateAsync(state);
            await AddToolAsync(printerId, 5, nozzleDiameter: 0.6f, hardened: true);

            // Act
            (JsonElement body, _) = await GetAsync(client, $"/api/v1/printers/{uuid}/telemetry");

            // Assert
            JsonElement[] tools = [.. body.GetProperty("tools").EnumerateArray()];

            tools.Select(tool => tool.GetProperty("toolNumber").GetInt32()).Should().Equal(1, 2, 5);
            tools.Select(tool => tool.GetProperty("picked").GetBoolean()).Should().Equal(false, false, true);
            tools[1].GetProperty("material").ValueKind.Should().Be(JsonValueKind.Null, "tool 2 is empty");
            tools[2].GetProperty("hardened").GetBoolean().Should().BeTrue();
            body.GetProperty("job").ValueKind.Should().Be(JsonValueKind.Null, "an idle printer reports no job");
        }
    }

    /// <summary>
    /// A printer that has never reported answers with nothing rather than with invented readings.
    /// </summary>
    [Fact]
    public async Task APrinterThatHasNeverReportedAnswersWithNothing()
    {
        // Arrange
        (Guid uuid, int _, HttpClient client) = await SeedAsync("silent@example.com");

        using (client)
        {
            // Act
            (JsonElement body, _) = await GetAsync(client, $"/api/v1/printers/{uuid}/telemetry");

            // Assert
            body.GetProperty("lastSeenAt").ValueKind.Should().Be(JsonValueKind.Null);
            body.GetProperty("job").ValueKind.Should().Be(JsonValueKind.Null);
            body.GetProperty("tools").GetArrayLength().Should().Be(0, "no tool is known, not one empty tool");
            body.GetProperty("temperatures").GetProperty("nozzle").GetProperty("current").ValueKind
                .Should().Be(JsonValueKind.Null);
        }
    }

    /// <summary>
    /// Without a window, the series covers the running print, as the printer page's graph does -
    /// and a reading that is a float comes out as one.
    /// </summary>
    [Fact]
    public async Task TheSeriesFollowsTheRunningPrintByDefault()
    {
        // Arrange
        (Guid uuid, int printerId, HttpClient client) = await SeedAsync("series@example.com");

        using (client)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;

            await AddLiveStateAsync(new PrinterLiveState
            {
                PrinterId = printerId,
                LastSeenAt = now,
                Status = PrinterStatus.Printing,
                JobId = 3,
                TimePrinting = 30 * 60,
            });

            // An hour of samples, a minute apart; the print is thirty minutes old.
            await AddSamplesAsync(printerId, now, TimeSpan.FromHours(1));

            // Act
            (JsonElement body, string raw) = await GetAsync(client, $"/api/v1/printers/{uuid}/telemetry/temperatures");

            // Assert
            DateTimeOffset from = body.GetProperty("from").GetDateTimeOffset();
            from.Should().BeAfter(now.AddMinutes(-31), "the window is the print's thirty minutes, not the idle hour");

            JsonElement[] points = [.. body.GetProperty("points").EnumerateArray()];
            points.Should().NotBeEmpty();
            points.Should().OnlyContain(point => point.GetProperty("at").GetDateTimeOffset() >= from);

            raw.Should().Contain("\"nozzle\":214.8,", "the aggregate is narrowed back to the float it averaged");
            raw.Should().NotContain("214.80000");
        }
    }

    /// <summary>
    /// An explicit window is honoured, one that ends before it starts is refused, and one longer
    /// than a day is cut to the day rather than refused.
    /// </summary>
    [Fact]
    public async Task AnExplicitWindowIsHonouredRefusedOrCut()
    {
        // Arrange
        (Guid uuid, int printerId, HttpClient client) = await SeedAsync("window@example.com");

        using (client)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await AddSamplesAsync(printerId, now, TimeSpan.FromHours(1));

            string Url(DateTimeOffset from, DateTimeOffset to)
            {
                return $"/api/v1/printers/{uuid}/telemetry/temperatures" +
                       $"?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
            }

            // Act
            (JsonElement narrow, _) = await GetAsync(client, Url(now.AddMinutes(-10), now));

            using HttpResponseMessage backwards = await client.GetAsync(Url(now, now.AddMinutes(-10)),
                                                                        TestContext.Current.CancellationToken);

            (JsonElement week, _) = await GetAsync(client, Url(now.AddDays(-7), now));

            // Assert
            narrow.GetProperty("points").EnumerateArray()
                  .Should().OnlyContain(point => point.GetProperty("at").GetDateTimeOffset() >= now.AddMinutes(-10).AddSeconds(-1));

            backwards.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            // The bucket is the window over ~180: a day's is eight minutes, so the hour of samples
            // lands in about eight of them, where a week's 56-minute buckets would hold it in two.
            week.GetProperty("points").GetArrayLength().Should().BeGreaterThanOrEqualTo(6,
                "the week is cut to the day ending now, which is what sets the bucket width");
        }
    }

    /// <summary>
    /// Another user's printer is not found by either endpoint, as it is not found anywhere else.
    /// </summary>
    [Fact]
    public async Task AnotherUsersPrinterIsNotFound()
    {
        // Arrange
        (Guid theirs, int _, HttpClient owner) = await SeedAsync("telemetry-owner@example.com");
        owner.Dispose();

        (Guid _, int _, HttpClient client) = await SeedAsync("telemetry-stranger@example.com");

        using (client)
        {
            // Act
            using HttpResponseMessage state = await client.GetAsync($"/api/v1/printers/{theirs}/telemetry",
                                                                    TestContext.Current.CancellationToken);
            using HttpResponseMessage series = await client.GetAsync($"/api/v1/printers/{theirs}/telemetry/temperatures",
                                                                     TestContext.Current.CancellationToken);

            // Assert
            state.StatusCode.Should().Be(HttpStatusCode.NotFound);
            series.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    private static async Task<(JsonElement body, string raw)> GetAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using JsonDocument payload = JsonDocument.Parse(raw);

        return (payload.RootElement.Clone(), raw);
    }

    private async Task AddLiveStateAsync(PrinterLiveState state)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        TelemetryDbContext telemetry = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();

        telemetry.PrinterLiveStates.Add(state);
        await telemetry.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A sample a minute across <paramref name="span"/>, ending at <paramref name="end"/>.</summary>
    private async Task AddSamplesAsync(int printerId, DateTimeOffset end, TimeSpan span)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        TelemetryDbContext telemetry = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();

        for (TimeSpan age = span; age >= TimeSpan.Zero; age -= TimeSpan.FromMinutes(1))
        {
            telemetry.TelemetrySamples.Add(new TelemetrySample
            {
                PrinterId = printerId,
                Timestamp = end - age,
                Status = PrinterStatus.Printing,
                NozzleTemperature = 214.8f,
                TargetNozzleTemperature = 215,
                BedTemperature = 60,
                TargetBedTemperature = 60,
            });
        }

        await telemetry.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>What the printer's <c>INFO</c> said about one of its tools.</summary>
    private async Task AddToolAsync(int printerId, int toolNumber, float nozzleDiameter, bool hardened = false)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        context.PrinterTools.Add(new PrinterTool
        {
            PrinterId = printerId,
            ToolNumber = toolNumber,
            NozzleDiameter = nozzleDiameter,
            Hardened = hardened,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<(Guid uuid, int printerId, HttpClient client)> SeedAsync(string email)
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, email);

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await context.TeamMembers
                                             .SingleAsync(m => m.UserId == user.Id, TestContext.Current.CancellationToken);

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            TeamId = membership.TeamId,
            Name = "Garage MK4",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (printer.Uuid, printer.Id, client);
    }
}
