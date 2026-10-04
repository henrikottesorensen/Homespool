using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.PrintFiles;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The pictures at the top of the printer page: a camera, and beside it the slicer's preview of what
/// is printing or a second camera - and the handler that serves the preview.
/// </summary>
/// <remarks>
/// What is asserted about the layout is what the server renders before any script runs. The script
/// takes over from exactly that state, so a wrong first render is a wrong first paint.
/// </remarks>
public sealed class PrinterPreviewTests : IAsyncLifetime
{
    /// <summary>How Razor renders a true <c>hidden</c>.</summary>
    private const string Hidden = "hidden=\"hidden\"";

    /// <summary>A real 1x1 PNG.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("printer-preview");

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
    /// The preview is served as the PNG in the file, marked private because it came through a
    /// sign-in, and cacheable because its address names one print's picture for good.
    /// </summary>
    [Fact]
    public async Task ThePreviewIsServedAsThePngInTheFile()
    {
        Seeded seeded = await SeedAsync("preview-served@example.com", cameras: 1, printing: true, withFile: true);

        using (seeded.Client)
        {
            using HttpResponseMessage response = await seeded.Client.GetAsync(ThumbnailUrl(seeded), TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be(MediaTypeNames.Image.Png);
            response.Headers.CacheControl!.Private.Should().BeTrue();

            byte[] body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

            body.Should().Equal(Png);
        }
    }

    /// <summary>
    /// Somebody who cannot see the printer is told nothing about it - the same 404 as a printer that
    /// does not exist, rather than a refusal confirming it does.
    /// </summary>
    [Fact]
    public async Task AnotherTeamIsToldThereIsNoSuchPreview()
    {
        Seeded seeded = await SeedAsync("preview-owner@example.com", cameras: 1, printing: true, withFile: true);
        seeded.Client.Dispose();

        (_, HttpClient stranger) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, "preview-stranger@example.com");

        using (stranger)
        {
            using HttpResponseMessage response = await stranger.GetAsync(ThumbnailUrl(seeded), TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// A print whose file is not stored here - one sent before, or deleted since - has no preview,
    /// and the page gives the place beside the camera to nothing rather than to a broken image.
    /// </summary>
    [Fact]
    public async Task APrintWithoutItsFileHasNoPreview()
    {
        Seeded seeded = await SeedAsync("preview-no-file@example.com", cameras: 1, printing: true, withFile: false);

        using (seeded.Client)
        {
            using HttpResponseMessage response = await seeded.Client.GetAsync(ThumbnailUrl(seeded), TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);

            string page = await GetAsync(seeded.Client, $"/Printers/Detail/{seeded.Printer}");

            DeckPreview(page).Should().Contain(Hidden, "there is nothing to show beside the camera");
            page.Should().NotContain($"data-print-thumbnail=\"{seeded.Print}\"");
        }
    }

    /// <summary>
    /// Printing with one camera: the camera, and the preview beside it. No switcher, because there is
    /// no other camera to step to.
    /// </summary>
    [Fact]
    public async Task APrintingPrinterShowsItsPreviewBesideTheCamera()
    {
        Seeded seeded = await SeedAsync("preview-beside@example.com", cameras: 1, printing: true, withFile: true);

        using (seeded.Client)
        {
            string page = await GetAsync(seeded.Client, $"/Printers/Detail/{seeded.Printer}");

            DeckPreview(page).Should().NotContain(Hidden);
            DeckPreview(page).Should().Contain("handler=Thumbnail").And.Contain($"printUuid={seeded.Print}");
            DeckCamera(page, 0).Should().NotContain(Hidden);
            page.Should().NotContain("camera-deck-switching");
        }
    }

    /// <summary>
    /// Printing with two cameras: one camera, the preview, and a switcher for the camera not on show.
    /// </summary>
    [Fact]
    public async Task ASecondCameraWaitsBehindTheSwitcherWhilePrinting()
    {
        Seeded seeded = await SeedAsync("preview-two-printing@example.com", cameras: 2, printing: true, withFile: true);

        using (seeded.Client)
        {
            string page = await GetAsync(seeded.Client, $"/Printers/Detail/{seeded.Printer}");

            DeckCamera(page, 0).Should().NotContain(Hidden);
            DeckCamera(page, 1).Should().Contain(Hidden);
            DeckPreview(page).Should().NotContain(Hidden);
            page.Should().Contain("camera-deck-switching");
            page.Should().Contain("1 of 2").And.Contain("2 of 2");
        }
    }

    /// <summary>
    /// Nothing printing, two cameras: both on show, side by side, and no switcher - every camera is
    /// already visible.
    /// </summary>
    [Fact]
    public async Task TwoCamerasShareThePlacesWhenNothingIsPrinting()
    {
        Seeded seeded = await SeedAsync("preview-two-idle@example.com", cameras: 2, printing: false, withFile: true);

        using (seeded.Client)
        {
            string page = await GetAsync(seeded.Client, $"/Printers/Detail/{seeded.Printer}");

            DeckCamera(page, 0).Should().NotContain(Hidden);
            DeckCamera(page, 1).Should().NotContain(Hidden);
            DeckPreview(page).Should().Contain(Hidden);
            page.Should().NotContain("camera-deck-switching");
        }
    }

    /// <summary>
    /// Three cameras and nothing printing: two on show and a switcher, because one is not.
    /// </summary>
    [Fact]
    public async Task AThirdCameraBringsTheSwitcherBack()
    {
        Seeded seeded = await SeedAsync("preview-three-idle@example.com", cameras: 3, printing: false, withFile: true);

        using (seeded.Client)
        {
            string page = await GetAsync(seeded.Client, $"/Printers/Detail/{seeded.Printer}");

            DeckCamera(page, 2).Should().Contain(Hidden);
            page.Should().Contain("camera-deck-switching");
        }
    }

    /// <summary>
    /// A printer with no camera still shows what it is printing: the preview alone, in the first place.
    /// </summary>
    [Fact]
    public async Task APrinterWithNoCameraShowsThePreviewAlone()
    {
        Seeded seeded = await SeedAsync("preview-no-camera@example.com", cameras: 0, printing: true, withFile: true);

        using (seeded.Client)
        {
            string page = await GetAsync(seeded.Client, $"/Printers/Detail/{seeded.Printer}");

            DeckRow(page).Should().NotContain(Hidden);
            DeckPreview(page).Should().NotContain(Hidden);
            DeckPreview(page).Should().Contain($"printUuid={seeded.Print}");
        }
    }

    /// <summary>
    /// And with nothing printing, the row is hidden rather than left as an empty gap - but rendered, so
    /// a print starting while the page is open has somewhere to go.
    /// </summary>
    [Fact]
    public async Task APrinterWithNoCameraAndNothingPrintingHidesTheRow()
    {
        Seeded seeded = await SeedAsync("preview-no-camera-idle@example.com", cameras: 0, printing: false, withFile: true);

        using (seeded.Client)
        {
            string page = await GetAsync(seeded.Client, $"/Printers/Detail/{seeded.Printer}");

            DeckRow(page).Should().Contain(Hidden);
        }
    }

    /// <summary>
    /// The status card's poll carries the preview in a template, which is how the picture follows a
    /// print that starts or ends while the page is open.
    /// </summary>
    [Fact]
    public async Task TheStatusPollCarriesThePreview()
    {
        Seeded seeded = await SeedAsync("preview-poll@example.com", cameras: 1, printing: true, withFile: true);

        using (seeded.Client)
        {
            string fragment = await GetAsync(seeded.Client, $"/Printers/Detail/{seeded.Printer}?handler=Status");

            Match template = Regex.Match(fragment, "<template data-print-thumbnail-template>(.*?)</template>", RegexOptions.Singleline);

            template.Success.Should().BeTrue();
            template.Groups[1].Value.Should().Contain($"data-print-thumbnail=\"{seeded.Print}\"");
        }
    }

    private static string ThumbnailUrl(Seeded seeded)
    {
        return $"/Printers/Detail/{seeded.Printer}?handler=Thumbnail&printUuid={seeded.Print}";
    }

    /// <summary>The opening tag of the whole row of pictures.</summary>
    private static string DeckRow(string page)
    {
        Match row = Regex.Match(page, "<div[^>]*data-camera-deck=[^>]*>");

        row.Success.Should().BeTrue("the row is rendered on every printer's page");

        return row.Value;
    }

    /// <summary>The opening tag of a camera's column, which carries whether it is on show.</summary>
    private static string DeckCamera(string page, int index)
    {
        MatchCollection columns = Regex.Matches(page, "<div[^>]*data-deck-camera=[^>]*>");

        columns.Count.Should().BeGreaterThan(index);

        return columns[index].Value;
    }

    /// <summary>The preview's column: its opening tag and what is in it, up to the deck's end.</summary>
    private static string DeckPreview(string page)
    {
        Match column = Regex.Match(page, "<div[^>]*data-deck-thumbnail[ >].*?data-deck-thumbnail-slot>(.*?)</div>",
                                   RegexOptions.Singleline);

        column.Success.Should().BeTrue("the preview's place is rendered whenever there is a camera");

        return column.Value;
    }

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Seeded> SeedAsync(string email, int cameras, bool printing, bool withFile)
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(_factory, email);

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await context.TeamMembers
                                             .SingleAsync(member => member.UserId == user.Id, TestContext.Current.CancellationToken);

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            TeamId = membership.TeamId,
            Name = "Garage CORE One",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        for (int index = 0; index < cameras; index++)
        {
            context.Cameras.Add(new Camera
            {
                Uuid = Guid.NewGuid(),
                Name = $"Camera {index + 1}",
                Source = "rtsp://192.0.2.1/live",
                TeamId = membership.TeamId,
                PrinterId = printer.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        context.PrinterLiveStates.Add(new PrinterLiveState
        {
            PrinterId = printer.Id,
            Status = printing ? PrinterStatus.Printing : PrinterStatus.Idle,
            LastSeenAt = DateTimeOffset.UtcNow,
        });

        string? digest = null;

        if (withFile)
        {
            string encoded = Convert.ToBase64String(Png);
            string gcode = $"; thumbnail begin 380x285 {encoded.Length}\n; {encoded}\n; thumbnail end\n\nG28\n";

            PrintFileCatalog catalog = scope.ServiceProvider.GetRequiredService<PrintFileCatalog>();
            StoredFile file = await catalog.SaveAsync(Caller.Unscoped(user.Id), "cube.gcode",
                                                      new MemoryStream(Encoding.ASCII.GetBytes(gcode)), overwrite: false,
                                                      TestContext.Current.CancellationToken);

            digest = (await catalog.RowForAsync(user.Id, file, TestContext.Current.CancellationToken))?.Digest;
        }

        Guid print = Guid.NewGuid();

        context.PrintJobs.Add(new PrintJob
        {
            PrintUuid = print,
            PrinterId = printer.Id,
            FileName = "cube.gcode",
            Digest = digest,
            QueuedByUserId = user.Id,
            State = printing ? PrintState.Printing : PrintState.Finished,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            EndedAt = printing ? null : DateTimeOffset.UtcNow.AddMinutes(-1),
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new Seeded(printer.Uuid, print, client);
    }

    private sealed record Seeded(Guid Printer, Guid Print, HttpClient Client);
}
