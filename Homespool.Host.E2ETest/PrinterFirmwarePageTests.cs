using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
using Homespool.Host.Firmware;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// A printer's firmware page, through the real page: who reaches it, what a refusal says, and a real
/// Prusa image stored and offered end to end.
/// </summary>
public sealed class PrinterFirmwarePageTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("printer-firmware");

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
    /// The printer's manager is shown the way to the page and gets it, with the firmware the printer
    /// last reported.
    /// </summary>
    [Fact]
    public async Task AManagerIsShownTheWayToThePageAndGetsIt()
    {
        // Arrange
        (HSUser owner, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "firmware-manager@example.com");

        using (client)
        {
            (Guid uuid, _) = await SeedAsync(owner.Id, "7.1.0");

            // Act
            string detail = await GetAsync(client, $"/Printers/Detail/{uuid}");
            string page = await GetAsync(client, $"/Printers/Firmware/{uuid}");

            // Assert
            detail.Should().Contain($"/Printers/Firmware/{uuid}");
            page.Should().Contain("Firmware: Workshop Core One");
            page.Should().Contain("This printer runs firmware 6.8.1+12345.");
            page.Should().Contain("No stored firmware fits this printer yet.");

            // The progress of a flash refreshes itself only with this script, which the layout does not
            // load - and these tests read the page without running any, so its absence is asserted here.
            page.Should().Contain("/js/live-region.", "the file name carries a fingerprint");
            page.Should().Contain("/js/dismiss-alert.");
        }
    }

    /// <summary>
    /// Operating a printer is not managing it: an operator is neither shown the way nor let in, and
    /// somebody not on the printer's team cannot tell it exists.
    /// </summary>
    [Fact]
    public async Task OnlyTheManagerReachesThePage()
    {
        // Arrange
        (HSUser owner, HttpClient ownerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "firmware-owner@example.com");
        (HSUser operating, HttpClient operatorClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "firmware-operator@example.com");
        (_, HttpClient strangerClient) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "firmware-stranger@example.com");

        using (ownerClient)
        using (operatorClient)
        using (strangerClient)
        {
            (Guid uuid, int teamId) = await SeedAsync(owner.Id, "7.1.0");
            await JoinAsync(operating.Id, teamId, CapabilityPresets.Operator);

            // Act
            string operatorDetail = await GetAsync(operatorClient, $"/Printers/Detail/{uuid}");
            using HttpResponseMessage operatorPage = await operatorClient.GetAsync($"/Printers/Firmware/{uuid}",
                                                                                 TestContext.Current.CancellationToken);
            using HttpResponseMessage strangerPage = await strangerClient.GetAsync($"/Printers/Firmware/{uuid}",
                                                                                 TestContext.Current.CancellationToken);

            // Assert
            operatorDetail.Should().NotContain("/Printers/Firmware/");
            operatorDetail.Should().Contain("Firmware 6.8.1", "the version is still there to read, just not a way in");
            operatorPage.StatusCode.Should().Be(HttpStatusCode.Redirect);
            operatorPage.Headers.Location!.ToString().Should().Contain("/Account/AccessDenied");
            strangerPage.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Theory]
    [InlineData("firmware.bbf", "is not a firmware image Homespool can read")]
    [InlineData("firmware.gcode", "is not a firmware image: Prusa")]
    public async Task ARefusedUploadSaysWhy(string name, string expected)
    {
        // Arrange
        (HSUser owner, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "firmware-refused@example.com");

        using (client)
        {
            (Guid uuid, _) = await SeedAsync(owner.Id, "7.1.0");

            // Act
            string page = await UploadAsync(client, uuid, name, Encoding.ASCII.GetBytes("not firmware at all"));

            // Assert
            page.Should().Contain(expected);
            page.Should().Contain("No stored firmware fits this printer yet.");
        }
    }

    /// <summary>
    /// A real Prusa release from the repository's <c>firmware/</c> directory, uploaded through the
    /// page to a printer it was built for: verified with Prusa's own key, stored, and offered. Skipped
    /// where that directory holds no image, since Prusa's images are never committed.
    /// </summary>
    [Fact]
    public async Task APrusaReleaseIsStoredAndOffered()
    {
        // Arrange
        string? path = PrusaImages().FirstOrDefault();
        Assert.SkipWhen(path is null, "no Prusa .bbf in the repository's firmware directory");

        byte[] image = await File.ReadAllBytesAsync(path!, TestContext.Current.CancellationToken);
        PrusaFirmwareCheck check;

        await using (MemoryStream content = new(image, writable: false))
        {
            check = await PrusaFirmwareVerifier.Prusa.CheckAsync(content, TestContext.Current.CancellationToken);
        }

        (HSUser owner, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "firmware-release@example.com");

        using (client)
        {
            (Guid uuid, _) = await SeedAsync(owner.Id, PrusaFirmwareCompatibility.BuildOf(check.Header!));

            // Act
            string page = await UploadAsync(client, uuid, Path.GetFileName(path!), image);
            string afterDelete = await DeleteAsync(client, uuid, DigestOn(page));

            // Assert
            page.Should().Contain($"firmware {check.Header!.Version}.");
            page.Should().NotContain("No stored firmware fits this printer yet.");
            afterDelete.Should().Contain($"Deleted {Path.GetFileName(path!)}.");
            afterDelete.Should().Contain("No stored firmware fits this printer yet.");
        }
    }

    [Fact]
    public async Task DeletingAnImageThatIsNotStoredSaysSo()
    {
        // Arrange
        (HSUser owner, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "firmware-delete-gone@example.com");

        using (client)
        {
            (Guid uuid, _) = await SeedAsync(owner.Id, "7.1.0");

            // Act
            string page = await DeleteAsync(client, uuid, "not-a-stored-digest");

            // Assert
            page.Should().Contain("That firmware image is no longer stored here.");
        }
    }

    private static string DigestOn(string page)
    {
        const string marker = "name=\"digest\" value=\"";
        int start = page.IndexOf(marker, StringComparison.Ordinal) + marker.Length;

        return page[start..page.IndexOf('"', start)];
    }

    /// <summary>Deletes through the page's form and returns the page the redirect lands on.</summary>
    private static async Task<string> DeleteAsync(HttpClient client, Guid uuid, string digest)
    {
        string form = await GetAsync(client, $"/Printers/Firmware/{uuid}");

        using FormUrlEncodedContent content = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(form),
            ["digest"] = digest,
        });

        using HttpResponseMessage response = await client.PostAsync($"/Printers/Firmware/{uuid}?handler=Delete", content,
                                                                    TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        return await GetAsync(client, response.Headers.Location!.OriginalString);
    }

    private static IEnumerable<string> PrusaImages()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Homespool.slnx")))
        {
            directory = directory.Parent;
        }

        string firmware = Path.Combine(directory?.FullName ?? ".", "firmware");

        return Directory.Exists(firmware) ? Directory.EnumerateFiles(firmware, "*.bbf") : [];
    }

    /// <summary>Uploads through the page's form and returns the page the redirect lands on.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification =
                         "MultipartFormDataContent takes ownership of the parts added to it and disposes them with itself, which the using declaration below does.")]
    private static async Task<string> UploadAsync(HttpClient client, Guid uuid, string name, byte[] bytes)
    {
        string form = await GetAsync(client, $"/Printers/Firmware/{uuid}");

        using MultipartFormDataContent content = [];
        content.Add(new StringContent(AntiforgeryTestHelper.ExtractToken(form)), "__RequestVerificationToken");
        content.Add(new ByteArrayContent(bytes), "file", name);

        using HttpResponseMessage response = await client.PostAsync($"/Printers/Firmware/{uuid}?handler=Upload", content,
                                                                    TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        return await GetAsync(client, response.Headers.Location!.OriginalString);
    }

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Decoded, because Razor encodes a version's '+' and a sentence's apostrophe, and the
        // assertions are about what a person reads.
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A printer on the owner's team, reporting <paramref name="model"/> and firmware 6.8.1.</summary>
    private async Task<(Guid uuid, int teamId)> SeedAsync(long ownerId, string model)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await context.TeamMembers
                                             .SingleAsync(m => m.UserId == ownerId, TestContext.Current.CancellationToken);

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            TeamId = membership.TeamId,
            Name = "Workshop Core One",
            Model = model,
            Firmware = "6.8.1+12345",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
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
