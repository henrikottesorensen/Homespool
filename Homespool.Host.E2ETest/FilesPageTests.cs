using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AwesomeAssertions;
using Homespool.Host.Services;
using Homespool.Model.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The Files page, driven the way a browser drives it: render, follow a link, submit a form.
/// </summary>
/// <remarks>
/// <para>
/// Razor views compile at request time, so a green build says nothing about whether this page
/// renders at all - these tests are what notice a broken tag helper or a handler name that no form
/// posts to. The store's own rules are covered in <c>UserFileStoreTests</c> and are not repeated.
/// </para>
/// <para>
/// Files are created through the API rather than by writing to disk, so a test exercises the same
/// path a user does and cannot invent a layout the store would not have produced.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class FilesPageTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-filespage-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Dispose();

        return Task.CompletedTask;
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
    public async Task ThePageListsTheCallersFilesAndNobodyElses()
    {
        // Arrange
        (HSUser _, HttpClient alice) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pagealice@example.com");
        (HSUser _, HttpClient bob) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pagebob@example.com");

        await UploadAsync(alice, "benchy.gcode", 2048);
        await UploadAsync(bob, "secret.gcode", 512);

        // Act
        string page = await (await alice.GetAsync("/Files")).Content.ReadAsStringAsync();

        // Assert
        page.Should().Contain("benchy.gcode");
        page.Should().NotContain("secret.gcode", "the listing is scoped to the signed-in user");
        page.Should().Contain("2 KB", "sizes are rendered for people, not in bytes");

        alice.Dispose();
        bob.Dispose();
    }

    [Fact]
    public async Task AnEmptyListSaysSoRatherThanRenderingAnEmptyTable()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pageempty@example.com");

        string page = await (await client.GetAsync("/Files")).Content.ReadAsStringAsync();

        page.Should().Contain("No files yet");

        client.Dispose();
    }

    /// <summary>
    /// The sort links are the whole feature: each heading has to produce a different order, and
    /// clicking the active one has to reverse it rather than do nothing.
    /// </summary>
    [Fact]
    public async Task SortingReordersTheTableAndTheActiveHeadingReverses()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pagesort@example.com");

        await UploadAsync(client, "big.gcode", 4096);
        await UploadAsync(client, "small.gcode", 128);

        // Act
        string bySizeDesc = await (await client.GetAsync("/Files?sort=size&desc=true")).Content.ReadAsStringAsync();
        string bySizeAsc = await (await client.GetAsync("/Files?sort=size&desc=false")).Content.ReadAsStringAsync();

        // Assert
        bySizeDesc.IndexOf("big.gcode", StringComparison.Ordinal)
            .Should().BeLessThan(bySizeDesc.IndexOf("small.gcode", StringComparison.Ordinal),
                "descending by size puts the largest first");

        bySizeAsc.IndexOf("small.gcode", StringComparison.Ordinal)
            .Should().BeLessThan(bySizeAsc.IndexOf("big.gcode", StringComparison.Ordinal),
                "and the same heading clicked again reverses it");

        client.Dispose();
    }

    /// <summary>
    /// The headings have to actually render, which is not the same as the sort working.
    /// </summary>
    /// <remarks>
    /// <b>Razor leaves <c>Word@Expression</c> alone, because it looks like an email address.</b> That
    /// shipped: the headings read <c>Name@Model.IndicatorFor(IndexModel.Columns.Name)</c> on the page
    /// while every sorting test passed, because those assert on row order and never look at the
    /// heading. Explicit parentheses - <c>@(...)</c> - are what stop the heuristic. Asserting the
    /// absence of <c>@Model</c> catches the whole family rather than this one instance.
    /// </remarks>
    [Fact]
    public async Task NoRazorExpressionLeaksIntoThePage()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pageleak@example.com");

        await UploadAsync(client, "rendered.gcode", 128);

        string page = await (await client.GetAsync("/Files")).Content.ReadAsStringAsync();

        page.Should().NotContain("@Model", "an unevaluated expression means Razor treated it as text");
        page.Should().NotContain("IndexModel.Columns", "which is how the sort indicators first shipped");
        page.Should().Contain(">Name", "and the heading itself still has to be there");

        client.Dispose();
    }

    /// <summary>
    /// The upload area still works with no scripting, and carries the hook that enhances it.
    /// </summary>
    /// <remarks>
    /// A test host runs no JavaScript, so this asserts what it can: that the drop zone's hook is
    /// present for <c>site.js</c> to find, and - more importantly - that the plain picker and its
    /// form are inside it. The drop only fills that input; if this test passes, uploading works
    /// whether or not the script ever runs.
    /// </remarks>
    [Fact]
    public async Task TheUploadFormWorksWithoutScriptAndCarriesTheDropHook()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pagedrop@example.com");

        string page = await (await client.GetAsync("/Files")).Content.ReadAsStringAsync();

        page.Should().Contain("data-upload-dropzone", "site.js finds the zone by this attribute");
        page.Should().Contain("""type="file" name="file" """.TrimEnd(),
            "and the drop only fills this input, so it has to be the thing that posts");
        page.Should().Contain("enctype=\"multipart/form-data\"");

        client.Dispose();
    }

    [Fact]
    public async Task AnUnknownSortColumnFallsBackRatherThanFailing()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pagebadsort@example.com");

        await UploadAsync(client, "one.gcode", 64);

        using HttpResponseMessage response = await client.GetAsync("/Files?sort=nonsense&desc=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a hand-edited query string is not an error worth a page of its own");

        client.Dispose();
    }

    [Fact]
    public async Task DeletingFromThePageRemovesTheFileAndKeepsTheSortOrder()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pagedelete@example.com");

        await UploadAsync(client, "doomed.gcode", 256);
        await UploadAsync(client, "keeper.gcode", 256);

        string page = await (await client.GetAsync("/Files?sort=name&desc=true")).Content.ReadAsStringAsync();

        // Act
        using FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
        });

        using HttpResponseMessage response = await client.PostAsync(
            "/Files?handler=Delete&name=doomed.gcode&sort=name&desc=true", form);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("sort=name")
            .And.Contain("desc=True", "the chosen order has to survive the redirect, or it silently resets");

        string after = await (await client.GetAsync("/Files")).Content.ReadAsStringAsync();
        after.Should().Contain("Deleted doomed.gcode.", "the page confirms what it did");

        // Asserted against the API rather than the rendered page: the confirmation message names the
        // file it just deleted, so "the page no longer mentions it" would be false for a working
        // delete. What is being checked is the store, and that is what the listing reports.
        string listing = await (await client.GetAsync("/api/v1/files")).Content.ReadAsStringAsync();
        listing.Should().NotContain("doomed.gcode");
        listing.Should().Contain("keeper.gcode");

        client.Dispose();
    }

    [Fact]
    public async Task RenamingFromThePageMovesTheFile()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pagerename@example.com");

        await UploadAsync(client, "before.gcode", 256);

        // The rename row is a query-string mode, so this is also what proves that link works.
        string editing = await (await client.GetAsync("/Files?rename=before.gcode")).Content.ReadAsStringAsync();
        editing.Should().Contain("newName", "following Rename puts an input in the row");

        // Act
        using FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(editing)),
            new("newName", "after.gcode"),
        });

        using HttpResponseMessage response = await client.PostAsync(
            "/Files?handler=Rename&name=before.gcode", form);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        string after = await (await client.GetAsync("/Files")).Content.ReadAsStringAsync();
        after.Should().Contain("after.gcode");
        after.Should().NotContain("before.gcode");

        client.Dispose();
    }

    [Fact]
    public async Task RenamingOntoATakenNameIsRefusedAndSaysWhy()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pageclash@example.com");

        await UploadAsync(client, "one.gcode", 256);
        await UploadAsync(client, "two.gcode", 256);

        string editing = await (await client.GetAsync("/Files?rename=one.gcode")).Content.ReadAsStringAsync();

        // Act
        using FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(editing)),
            new("newName", "two.gcode"),
        });

        using HttpResponseMessage response = await client.PostAsync(
            "/Files?handler=Rename&name=one.gcode", form);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        string after = await (await client.GetAsync("/Files")).Content.ReadAsStringAsync();
        after.Should().Contain("already exists", "the conflict is explained rather than swallowed");
        after.Should().Contain("one.gcode", "and nothing moved");

        client.Dispose();
    }

    [Fact]
    public async Task ThePageIsNotAnonymous()
    {
        using HttpClient client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using HttpResponseMessage response = await client.GetAsync("/Files");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/Account/Login",
            "a page, unlike /api, sends a browser somewhere useful");
    }

    /// <summary>
    /// Uploading through the page, which is the whole reason the page exists: the empty state told
    /// people to upload a model and, until this handler, offered nothing to do it with.
    /// </summary>
    [Fact]
    public async Task UploadingThroughThePageStoresTheFile()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pageupload@example.com");

        string page = await (await client.GetAsync("/Files")).Content.ReadAsStringAsync();

        // Act
        using HttpResponseMessage response = await PostFileAsync(client, page, "uploaded.gcode", "G28 ; home");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        string after = await (await client.GetAsync("/Files")).Content.ReadAsStringAsync();
        after.Should().Contain("Uploaded uploaded.gcode.");
        after.Should().Contain("10 B", "the size comes from the bytes that actually arrived");

        client.Dispose();
    }

    /// <summary>
    /// The conflict flow end to end. The point of staging is that answering "replace it" does not
    /// re-send the file, so this also asserts the replacement content is the one that was uploaded
    /// first - proving the held bytes are what gets published, not a second copy.
    /// </summary>
    [Fact]
    public async Task AClashAsksBeforeReplacingAndThenUsesTheBytesAlreadySent()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pageclash2@example.com");

        string first = await (await client.GetAsync("/Files")).Content.ReadAsStringAsync();
        (await PostFileAsync(client, first, "benchy.gcode", "original")).Dispose();

        string listed = await (await client.GetAsync("/Files")).Content.ReadAsStringAsync();

        // Act
        (await PostFileAsync(client, listed, "benchy.gcode", "replacement")).Dispose();

        string asked = await (await client.GetAsync("/Files")).Content.ReadAsStringAsync();

        // Assert
        asked.Should().Contain("You already have a file named");
        asked.Should().Contain("Replace it");

        string token = Regex.Match(asked, """token=([A-Za-z0-9]{32})""").Groups[1].Value;
        token.Should().NotBeEmpty("the prompt has to carry the handle to the staged bytes");

        using FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(asked)),
        });

        using HttpResponseMessage replaced = await client.PostAsync($"/Files?handler=Replace&token={token}", form);

        replaced.StatusCode.Should().Be(HttpStatusCode.Redirect);

        string content = await (await client.GetAsync("/api/v1/files/benchy.gcode")).Content.ReadAsStringAsync();
        content.Should().Be("replacement", "the bytes held during the question are the ones published");

        client.Dispose();
    }

    [Fact]
    public async Task DecliningTheReplacementKeepsTheOriginal()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pagedecline@example.com");

        string first = await (await client.GetAsync("/Files")).Content.ReadAsStringAsync();
        (await PostFileAsync(client, first, "keep.gcode", "original")).Dispose();

        string listed = await (await client.GetAsync("/Files")).Content.ReadAsStringAsync();
        (await PostFileAsync(client, listed, "keep.gcode", "replacement")).Dispose();

        string asked = await (await client.GetAsync("/Files")).Content.ReadAsStringAsync();
        string token = Regex.Match(asked, """token=([A-Za-z0-9]{32})""").Groups[1].Value;

        // Act
        using FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(asked)),
        });

        using HttpResponseMessage discarded = await client.PostAsync($"/Files?handler=Discard&token={token}", form);

        // Assert
        discarded.StatusCode.Should().Be(HttpStatusCode.Redirect);

        string content = await (await client.GetAsync("/api/v1/files/keep.gcode")).Content.ReadAsStringAsync();
        content.Should().Be("original", "declining leaves what was already there untouched");

        client.Dispose();
    }

    [Fact]
    public async Task AnExtensionNoPrinterAcceptsIsRefusedByThePage()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pagebadext@example.com");

        string page = await (await client.GetAsync("/Files")).Content.ReadAsStringAsync();

        (await PostFileAsync(client, page, "firmware.bbf", "not gcode")).Dispose();

        string after = await (await client.GetAsync("/Files")).Content.ReadAsStringAsync();
        after.Should().Contain("not a file a printer would accept");
        after.Should().Contain("No files yet", "and nothing was stored");

        client.Dispose();
    }

    /// <summary>Posts the upload form the way a browser would: multipart, with the antiforgery field.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification = "MultipartFormDataContent takes ownership of the parts added to it and disposes them with itself, which the using declaration below does.")]
    private static async Task<HttpResponseMessage> PostFileAsync(HttpClient client, string page, string name, string content)
    {
        // Disposing the MultipartFormDataContent disposes the parts it owns, which is why neither the
        // token nor the file content is disposed separately here.
        using MultipartFormDataContent form = [];

        form.Add(new StringContent(AntiforgeryTestHelper.ExtractToken(page)), "__RequestVerificationToken");
        form.Add(new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(content))), "file", name);

        return await client.PostAsync("/Files?handler=Upload", form);
    }

    private static async Task UploadAsync(HttpClient client, string name, int bytes)
    {
        using StreamContent body = new(new MemoryStream(Encoding.UTF8.GetBytes(new string('G', bytes))));
        using HttpResponseMessage response = await client.PutAsync($"/api/v1/files/{name}", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture upload has to have worked");
    }
}
