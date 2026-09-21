using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
/// What <c>/api/v1/files</c> says about each file: its digest, and what it says it was sliced for.
/// </summary>
/// <remarks>
/// <para>
/// <b>The four metadata states are the point</b>, because three of them leave every field null and
/// mean different things - a file from another slicer, a file nothing could parse, and a file nobody
/// has read. Each is produced here the way it arises: a PrusaSlicer config block, plain gcode, a file
/// too short to carry a header, and a row written without reading (as the startup reconcile writes
/// them) or no row at all.
/// </para>
/// <para>
/// Floats are asserted on the raw body, where a widened value would show.
/// </para>
/// </remarks>
public sealed class FileMetadataEndpointTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("file-metadata");
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
    /// The upload answers with what the reader found in the bytes just written - and with no path on
    /// a printer, which is a fact about each printer rather than about the file.
    /// </summary>
    [Fact]
    public async Task AnUploadReportsWhatTheFileSaysItWasSlicedFor()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "sliced@example.com");

        using (client)
        {
            // Act
            (JsonElement file, string raw) = await UploadAsync(client, "coreone.gcode", Sliced());

            // Assert
            JsonElement metadata = file.GetProperty("metadata");
            metadata.GetProperty("state").GetString().Should().Be("Read");
            metadata.GetProperty("printerModel").GetString().Should().Be("COREONE");
            metadata.GetProperty("extruderCount").GetInt32().Should().Be(1);
            metadata.GetProperty("filamentTypes").EnumerateArray().Select(type => type.GetString())
                    .Should().Equal("PLA", "PETG");
            metadata.GetProperty("requiresHardenedNozzle").GetBoolean().Should().BeTrue();
            metadata.GetProperty("requiresHighFlowNozzle").GetBoolean().Should().BeFalse();

            file.GetProperty("digest").GetString().Should().NotBeNullOrEmpty();
            file.TryGetProperty("printerPath", out _).Should().BeFalse("a printer's path is that printer's to report");

            raw.Should().Contain("\"nozzleDiameter\":0.4,", "a float must not print its double widening");
        }
    }

    /// <summary>
    /// The listing tells apart the four reasons a file's details may be empty.
    /// </summary>
    [Fact]
    public async Task TheListingTellsTheFourStatesApart()
    {
        // Arrange
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "states@example.com");

        using (client)
        {
            await UploadAsync(client, "read.gcode", Sliced());
            await UploadAsync(client, "silent.gcode", "G28 ; home\nG1 X10 Y10\n");
            await UploadAsync(client, "broken.gcode", "G1\n");
            await UploadAsync(client, "reconciled.gcode", "G28 ; home\n");
            await UploadAsync(client, "unindexed.gcode", "G28 ; home\n");

            // As the startup reconcile leaves a file it found on disk: a row, never read. And a file
            // with no row at all, which the disk still says exists.
            await ChangeRowsAsync(user.Id, rows =>
            {
                PrintFile reconciled = rows.Single(row => row.Name == "reconciled.gcode");
                reconciled.MetadataState = PrintFileMetadataState.Undefined;
                reconciled.Digest = null;

                return rows.Single(row => row.Name == "unindexed.gcode");
            });

            // Act
            JsonElement[] files = await ListAsync(client);

            // Assert
            StateOf(files, "read.gcode").Should().Be("Read");
            StateOf(files, "silent.gcode").Should().Be("Silent", "plain gcode is ordinary output from another slicer");
            StateOf(files, "broken.gcode").Should().Be("Unreadable", "three bytes carry no header to read");
            StateOf(files, "reconciled.gcode").Should().Be("Unread", "a row nobody wrote a state on is a row nobody read");
            StateOf(files, "unindexed.gcode").Should().Be("Unread", "a file with no row is still listed");

            Named(files, "reconciled.gcode").GetProperty("digest").ValueKind.Should().Be(JsonValueKind.Null);
            Named(files, "silent.gcode").GetProperty("metadata").GetProperty("printerModel").ValueKind
                                        .Should().Be(JsonValueKind.Null);
        }
    }

    private static string? StateOf(JsonElement[] files, string name)
    {
        return Named(files, name).GetProperty("metadata").GetProperty("state").GetString();
    }

    private static JsonElement Named(JsonElement[] files, string name)
    {
        return files.Single(file => file.GetProperty("name").GetString() == name);
    }

    /// <summary>A PrusaSlicer config block, as the slicer writes it at the end of a file.</summary>
    private static string Sliced()
    {
        return "G28 ; home\nG1 X10 Y10 F3000\n\n" +
               "; prusaslicer_config = begin\n" +
               "; filament_abrasive = 0,1\n" +
               "; filament_type = PLA;PETG\n" +
               "; nozzle_diameter = 0.4\n" +
               "; nozzle_high_flow = 0\n" +
               "; printer_model = COREONE\n" +
               "; prusaslicer_config = end\n";
    }

    private static async Task<(JsonElement file, string raw)> UploadAsync(HttpClient client, string name, string content)
    {
        using StreamContent body = new(new MemoryStream(Encoding.UTF8.GetBytes(content)));
        using HttpResponseMessage response = await client.PutAsync($"/api/v1/files/{name}", body,
                                                                    TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using JsonDocument payload = JsonDocument.Parse(raw);

        return (payload.RootElement.Clone(), raw);
    }

    private static async Task<JsonElement[]> ListAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/api/v1/files", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument payload =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return [.. payload.RootElement.EnumerateArray().Select(file => file.Clone())];
    }

    /// <summary>Edits the user's rows in place, and removes the one <paramref name="edit"/> returns.</summary>
    private async Task ChangeRowsAsync(long userId, Func<List<PrintFile>, PrintFile> edit)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext database = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        List<PrintFile> rows =
            await database.PrintFiles.Where(row => row.UserId == userId).ToListAsync(TestContext.Current.CancellationToken);

        database.PrintFiles.Remove(edit(rows));

        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
