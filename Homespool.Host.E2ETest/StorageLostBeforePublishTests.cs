using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Pages;
using Homespool.Host.PrintFiles;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

using FilesIndexModel = Homespool.Host.Pages.Files.IndexModel;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The print-file storage going away after an upload was staged and before it was published: the
/// page answers with the storage sentence, publishes nothing and keeps none of the staged bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replace goes through the real pipeline</b>, because its window is as long as somebody takes
/// to answer the question: the marker is removed while the question is open.
/// </para>
/// <para>
/// <b>Upload and the tile drop are called directly</b>, on services from this host's container,
/// because their window is inside one request - between the stage and the publish of the same
/// handler - and nothing a client sends can land there. The file handed to them removes the marker
/// when its stream is closed, which both do as soon as staging has read it.
/// </para>
/// </remarks>
public sealed class StorageLostBeforePublishTests : IAsyncLifetime
{
    private const string StorageSentence = "File storage is not available on this server, so nothing was uploaded.";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("storagelost");
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
    /// <b>"Replace it" after the storage has gone is refused, not a 500</b>, the original is left
    /// alone, and the staged bytes are gone: answering again once the storage is back finds nothing
    /// waiting.
    /// </summary>
    [Fact]
    public async Task ReplacingAfterTheStorageHasGoneIsRefusedAndDiscardsTheStagedUpload()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "lostreplace@example.com");

        (await PostFileAsync(client, await FilesPageAsync(client), "benchy.gcode", "original")).Dispose();
        (await PostFileAsync(client, await FilesPageAsync(client), "benchy.gcode", "replacement")).Dispose();

        string asked = await FilesPageAsync(client);
        string token = Regex.Match(asked, """token=([A-Za-z0-9]{32})""").Groups[1].Value;
        token.Should().NotBeEmpty("the clash has to have been asked about for there to be a Replace to answer");

        File.Delete(MarkerPath());

        // Act
        using HttpResponseMessage replaced = await PostReplaceAsync(client, asked, token);

        // Assert
        replaced.StatusCode.Should().Be(HttpStatusCode.Redirect, "a refusal is an answer, not a 500");

        string after = await FilesPageAsync(client);
        after.Should().Contain(StorageSentence);

        await File.WriteAllTextAsync(MarkerPath(), string.Empty, TestContext.Current.CancellationToken);

        string content =
            await (await client.GetAsync("/api/v1/files/benchy.gcode", TestContext.Current.CancellationToken)).Content
                .ReadAsStringAsync(TestContext.Current.CancellationToken);
        content.Should().Be("original", "nothing was published over it");

        (await PostReplaceAsync(client, after, token)).Dispose();

        (await FilesPageAsync(client)).Should().Contain("That upload is no longer waiting",
                                                        "the refused answer threw the staged bytes away");

        client.Dispose();
    }

    /// <summary>
    /// <b>An upload whose storage goes between staging and publishing is refused, not a 500</b>,
    /// and leaves nothing behind - no file under its name and no staged bytes waiting for the sweep.
    /// </summary>
    [Fact]
    public async Task AnUploadWhoseStorageGoesBeforePublishingIsRefusedAndKeepsNothing()
    {
        // Arrange
        (HSUser enrolled, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "lostupload@example.com");
        client.Dispose();

        using IServiceScope scope = _factory.Services.CreateScope();
        ClaimsPrincipal principal = await PrincipalAsync(scope, enrolled.Id);

        FilesIndexModel model = ActivatorUtilities.CreateInstance<FilesIndexModel>(scope.ServiceProvider);
        model.PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext { User = principal, RequestServices = scope.ServiceProvider },
        };

        // Act
        IActionResult result = await InEnglishAsync(() => model.OnPostUploadAsync(new StorageLostAfterStaging("lost.gcode", MarkerPath()),
                                                                                  sort: null,
                                                                                  desc: null,
                                                                                  printerUuid: null,
                                                                                  compatible: null,
                                                                                  TestContext.Current.CancellationToken));

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        model.StatusMessage.Should().StartWith(StorageSentence);
        model.StatusSuccess.Should().BeFalse();
        model.PendingToken.Should().BeNull("this is not a name clash, so there is no question to ask");

        AssertNothingKept(scope, enrolled.Id, "lost.gcode");
    }

    /// <summary>
    /// <b>A tile drop whose storage goes between staging and publishing says so for that file</b>,
    /// rather than ending the whole drop in a 500, and leaves nothing behind.
    /// </summary>
    [Fact]
    public async Task ATileDropWhoseStorageGoesBeforePublishingReportsItAndKeepsNothing()
    {
        // Arrange
        (HSUser enrolled, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "lostdrop@example.com");
        client.Dispose();

        using IServiceScope scope = _factory.Services.CreateScope();
        Caller caller = Caller.Unscoped(enrolled.Id);

        PrinterWithState? row = await scope.ServiceProvider.GetRequiredService<PrinterQueryService>()
                                           .GetPrinterWithStateForUserAsync(SeedPrinter(enrolled.Id), caller,
                                                                            TestContext.Current.CancellationToken);
        row.Should().NotBeNull("the drop needs a printer the dropper can see");

        TileDrop drop = scope.ServiceProvider.GetRequiredService<TileDrop>();

        // Act
        (string message, _) = await InEnglishAsync(() => drop.DropAsync(row!,
                                                                        caller,
                                                                        TileDrop.Upload,
                                                                        [new StorageLostAfterStaging("boat.gcode", MarkerPath())],
                                                                        [],
                                                                        enrolled.UserName,
                                                                        TestContext.Current.CancellationToken));

        // Assert
        message.Should().StartWith($"boat.gcode was not uploaded: {StorageSentence}");

        AssertNothingKept(scope, enrolled.Id, "boat.gcode");
    }

    /// <summary>Where this host's storage keeps the file that says it is the real storage.</summary>
    private string MarkerPath()
    {
        return Path.Combine(StorageRoot(), UserFileStore.MarkerFileName);
    }

    /// <summary>The storage root, resolved the way the store resolves it.</summary>
    private string StorageRoot()
    {
        string directory = _factory.Services.GetRequiredService<IOptions<PrintFileStorageOptions>>().Value.Directory;

        return Path.Combine(_factory.Services.GetRequiredService<IHostEnvironmentAccessor>().ContentRootPath, directory);
    }

    /// <summary>No file under <paramref name="name"/>, and no staged upload left in <c>.incoming</c>.</summary>
    private void AssertNothingKept(IServiceScope scope, long userId, string name)
    {
        scope.ServiceProvider.GetRequiredService<UserFileStore>().Find(userId, name).Should().BeNull("nothing was published");

        string incoming = Path.Combine(StorageRoot(), ".incoming");

        (Directory.Exists(incoming) ? Directory.EnumerateFiles(incoming).ToList() : [])
            .Should().BeEmpty("the staged bytes are discarded with the refusal, not left for the sweep");
    }

    /// <summary>The principal a sign-in would give this user, which is what the page reads its caller from.</summary>
    private static async Task<ClaimsPrincipal> PrincipalAsync(IServiceScope scope, long userId)
    {
        UserManager<HSUser> users = scope.ServiceProvider.GetRequiredService<UserManager<HSUser>>();
        HSUser user = (await users.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture)))!;

        return await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<HSUser>>().CreateAsync(user);
    }

    /// <summary>
    /// Runs a handler under English, which the pipeline would have chosen from the request and a direct
    /// call does not - so the sentences asserted on do not depend on the culture of the machine.
    /// </summary>
    private static async Task<T> InEnglishAsync<T>(Func<Task<T>> handler)
    {
        CultureInfo before = CultureInfo.CurrentUICulture;

        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-GB");

        try
        {
            return await handler();
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    /// <summary>One printer on the team registration already made for this user.</summary>
    private Guid SeedPrinter(long userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        int teamId = context.TeamMembers.First(member => member.UserId == userId).TeamId;

        Printer printer = new() { Uuid = Guid.NewGuid(), TeamId = teamId, Name = "Drop Target" };
        context.Printers.Add(printer);
        context.SaveChanges();

        return printer.Uuid;
    }

    private static async Task<string> FilesPageAsync(HttpClient client)
    {
        return await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                   TestContext.Current.CancellationToken);
    }

    /// <summary>Posts the upload form the way a browser would: multipart, with the antiforgery field.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification =
                         "MultipartFormDataContent takes ownership of the parts added to it and disposes them with itself, which the using declaration below does.")]
    private static async Task<HttpResponseMessage> PostFileAsync(HttpClient client, string page, string name, string content)
    {
        using MultipartFormDataContent form = [];

        form.Add(new StringContent(AntiforgeryTestHelper.ExtractToken(page)), "__RequestVerificationToken");
        form.Add(new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(content))), "file", name);

        return await client.PostAsync("/Files?handler=Upload", form, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> PostReplaceAsync(HttpClient client, string page, string token)
    {
        using FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
        });

        return await client.PostAsync($"/Files?handler=Replace&token={token}", form, TestContext.Current.CancellationToken);
    }

    /// <summary>An uploaded file whose storage loses its marker as soon as the upload has been staged.</summary>
    /// <remarks>
    /// Closing the stream is the signal: staging reads it to the end, and both callers close it
    /// before they publish. The marker is gone only then, so staging itself passes its own check.
    /// </remarks>
    private sealed class StorageLostAfterStaging(string fileName, string markerPath) : IFormFile
    {
        private readonly byte[] _content = Encoding.UTF8.GetBytes("G28 ; home\n");

        public string ContentType => "application/octet-stream";

        public string ContentDisposition => $"form-data; name=\"file\"; filename=\"{fileName}\"";

        public IHeaderDictionary Headers { get; } = new HeaderDictionary();

        public long Length => _content.Length;

        public string Name => "file";

        public string FileName => fileName;

        public Stream OpenReadStream()
        {
            return new MarkerRemovingStream(_content, markerPath);
        }

        public void CopyTo(Stream target)
        {
            throw new NotSupportedException("Uploads are staged from OpenReadStream.");
        }

        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Uploads are staged from OpenReadStream.");
        }
    }

    private sealed class MarkerRemovingStream(byte[] content, string markerPath) : MemoryStream(content, writable: false)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                File.Delete(markerPath);
            }

            base.Dispose(disposing);
        }
    }
}
