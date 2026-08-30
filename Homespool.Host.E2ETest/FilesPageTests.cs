using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

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

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
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
        string page =
            await (await alice.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);

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

        string page =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);

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
        string bySizeDesc =
            await (await client.GetAsync("/Files?sort=size&desc=true", TestContext.Current.CancellationToken)).Content
                .ReadAsStringAsync(TestContext.Current.CancellationToken);
        string bySizeAsc =
            await (await client.GetAsync("/Files?sort=size&desc=false", TestContext.Current.CancellationToken)).Content
                .ReadAsStringAsync(TestContext.Current.CancellationToken);

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

        string page =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);

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
    /// present for <c>site.js</c> to find, and - more importantly - that the plain picker, its button
    /// and its form are inside it. With scripting off this is just a bordered box around a working
    /// file picker; with it, a picked file posts itself and the button goes unused.
    /// </remarks>
    [Fact]
    public async Task TheUploadFormWorksWithoutScriptAndCarriesTheDropHook()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pagedrop@example.com");

        string page =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);

        page.Should().Contain("data-upload-dropzone", "site.js finds the zone by this attribute");
        page.Should().Contain("""type="file" name="file" """.TrimEnd(),
                              "and the drop only fills this input, so it has to be the thing that posts");
        page.Should().Contain("enctype=\"multipart/form-data\"");
        page.Should().Contain("btn-primary text-nowrap", "the fallback for when the script never runs");

        client.Dispose();
    }

    /// <summary>
    /// With no printers there is nothing to send to, so the control is absent rather than empty.
    /// </summary>
    [Fact]
    public async Task TheSendControlIsAbsentWhenThereAreNoPrinters()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pagenoprinters@example.com");

        await UploadAsync(client, "lonely.gcode", 128);

        string page =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);

        page.Should().Contain("lonely.gcode");
        page.Should().NotContain("handler=Send", "a select with no options is worse than no select");

        client.Dispose();
    }

    /// <summary>
    /// The rework this class exists to pin: one printer selector for the whole page, not one per row.
    /// </summary>
    [Fact]
    public async Task ThereIsOnePrinterSelectorForTheWholePageNotOnePerRow()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pagepicker@example.com");
        await ClaimAPrinterAsync(client);

        await UploadAsync(client, "one.gcode", 128);
        await UploadAsync(client, "two.gcode", 128);

        // Act
        string page =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);

        // Assert
        Regex.Matches(page, "<select").Count.Should().Be(
            1, "two rows sharing one printer choice means one <select>, not one each");

        // Without this hook, picking a different printer only changes what the <select> shows -
        // the row buttons below were rendered against the selection in force when the page loaded,
        // and site.js's change listener is what makes choosing one actually retarget them.
        page.Should().Contain("data-printer-select", "site.js submits the selector on change through this hook");

        // The DB id in the selector's <option> is what every row's Send/Queue form has to carry -
        // as a route value, the same way sort and rename already are, rather than a select of its own.
        string printerId = Regex.Match(page, """<option value="(\d+)"[^>]*selected""").Groups[1].Value;
        printerId.Should().NotBeEmpty("the claimed printer has to be the one the selector shows chosen");

        Regex.Matches(page, $"printerId={printerId}").Count.Should().BeGreaterThanOrEqualTo(
            4, "the top selector plus both rows' Send and Queue buttons all carry the same id");

        client.Dispose();
    }

    /// <summary>
    /// Registers and claims a fake printer as the given, already-authenticated client - the same
    /// two calls <c>EndToEndEnrolmentTests</c> drives, kept minimal because this suite only needs a
    /// printer to exist for the caller, not the enrolment contract itself.
    /// </summary>
    private async Task ClaimAPrinterAsync(HttpClient client)
    {
        using HttpClient anonymous = PrinterListener.CreateClient(_factory);

        using HttpResponseMessage registerResponse = await EnrolmentFlowHelper.SendPrinterRegisterAsync(anonymous, new
        {
            sn = Guid.NewGuid().ToString("N"),
            fingerprint = Guid.NewGuid().ToString("N"),
            printer_type = "1.3.5",
            firmware = "6.4.0+11974",
        });

        registerResponse.EnsureSuccessStatusCode();
        string code = registerResponse.Headers.GetValues("Code").Single();

        using HttpResponseMessage claimResponse = await client.PostAsJsonAsync(
            "/api/v1/printers/register",
            new { name = "Picker printer", location = "Test bench", code },
            TestContext.Current.CancellationToken);

        claimResponse.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// A printer id typed into the form by hand finds nothing rather than someone else's machine -
    /// the send handler resolves it in the caller's own list, which is what scopes it.
    /// </summary>
    [Fact]
    public async Task SendingToAPrinterThatIsNotYoursIsRefused()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pagesend@example.com");

        await UploadAsync(client, "mine.gcode", 128);

        string page =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);

        using FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
            new("printerId", "4242"),
        });

        using HttpResponseMessage response = await client.PostAsync(
            "/Files?handler=Send&name=mine.gcode", form, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        string after =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
        after.Should().Contain("not one of yours");

        client.Dispose();
    }

    [Fact]
    public async Task AnUnknownSortColumnFallsBackRatherThanFailing()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pagebadsort@example.com");

        await UploadAsync(client, "one.gcode", 64);

        using HttpResponseMessage response =
            await client.GetAsync("/Files?sort=nonsense&desc=true", TestContext.Current.CancellationToken);

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

        string page = await (await client.GetAsync("/Files?sort=name&desc=true", TestContext.Current.CancellationToken)).Content
            .ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Act
        using FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(page)),
        });

        using HttpResponseMessage response = await client.PostAsync(
            "/Files?handler=Delete&name=doomed.gcode&sort=name&desc=true", form, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("sort=name")
                .And.Contain("desc=True", "the chosen order has to survive the redirect, or it silently resets");

        string after =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
        after.Should().Contain("Deleted doomed.gcode.", "the page confirms what it did");

        // Asserted against the API rather than the rendered page: the confirmation message names the
        // file it just deleted, so "the page no longer mentions it" would be false for a working
        // delete. What is being checked is the store, and that is what the listing reports.
        string listing =
            await (await client.GetAsync("/api/v1/files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
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
        string editing =
            await (await client.GetAsync("/Files?rename=before.gcode", TestContext.Current.CancellationToken)).Content
                .ReadAsStringAsync(TestContext.Current.CancellationToken);
        editing.Should().Contain("newName", "following Rename puts an input in the row");

        // Act
        using FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(editing)),
            new("newName", "after.gcode"),
        });

        using HttpResponseMessage response = await client.PostAsync(
            "/Files?handler=Rename&name=before.gcode", form, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        string after =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
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

        string editing =
            await (await client.GetAsync("/Files?rename=one.gcode", TestContext.Current.CancellationToken)).Content
                .ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Act
        using FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(editing)),
            new("newName", "two.gcode"),
        });

        using HttpResponseMessage response = await client.PostAsync(
            "/Files?handler=Rename&name=one.gcode", form, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        string after =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
        after.Should().Contain("already exists", "the conflict is explained rather than swallowed");
        after.Should().Contain("one.gcode", "and nothing moved");

        client.Dispose();
    }

    [Fact]
    public async Task ThePageIsNotAnonymous()
    {
        using HttpClient client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using HttpResponseMessage response = await client.GetAsync("/Files", TestContext.Current.CancellationToken);

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

        string page =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);

        // Act
        using HttpResponseMessage response = await PostFileAsync(client, page, "uploaded.gcode", "G28 ; home");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        string after =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
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

        string first =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
        (await PostFileAsync(client, first, "benchy.gcode", "original")).Dispose();

        string listed =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);

        // Act
        (await PostFileAsync(client, listed, "benchy.gcode", "replacement")).Dispose();

        string asked =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);

        // Assert
        asked.Should().Contain("You already have a file named");
        asked.Should().Contain("Replace it");

        string token = Regex.Match(asked, """token=([A-Za-z0-9]{32})""").Groups[1].Value;
        token.Should().NotBeEmpty("the prompt has to carry the handle to the staged bytes");

        using FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(asked)),
        });

        using HttpResponseMessage replaced =
            await client.PostAsync($"/Files?handler=Replace&token={token}", form, TestContext.Current.CancellationToken);

        replaced.StatusCode.Should().Be(HttpStatusCode.Redirect);

        string content =
            await (await client.GetAsync("/api/v1/files/benchy.gcode", TestContext.Current.CancellationToken)).Content
                .ReadAsStringAsync(TestContext.Current.CancellationToken);
        content.Should().Be("replacement", "the bytes held during the question are the ones published");

        client.Dispose();
    }

    [Fact]
    public async Task DecliningTheReplacementKeepsTheOriginal()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pagedecline@example.com");

        string first =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
        (await PostFileAsync(client, first, "keep.gcode", "original")).Dispose();

        string listed =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
        (await PostFileAsync(client, listed, "keep.gcode", "replacement")).Dispose();

        string asked =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
        string token = Regex.Match(asked, """token=([A-Za-z0-9]{32})""").Groups[1].Value;

        // Act
        using FormUrlEncodedContent form = new(new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", AntiforgeryTestHelper.ExtractToken(asked)),
        });

        using HttpResponseMessage discarded =
            await client.PostAsync($"/Files?handler=Discard&token={token}", form, TestContext.Current.CancellationToken);

        // Assert
        discarded.StatusCode.Should().Be(HttpStatusCode.Redirect);

        string content =
            await (await client.GetAsync("/api/v1/files/keep.gcode", TestContext.Current.CancellationToken)).Content
                .ReadAsStringAsync(TestContext.Current.CancellationToken);
        content.Should().Be("original", "declining leaves what was already there untouched");

        client.Dispose();
    }

    [Fact]
    public async Task AnExtensionNoPrinterAcceptsIsRefusedByThePage()
    {
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pagebadext@example.com");

        string page =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);

        (await PostFileAsync(client, page, "firmware.bbf", "not gcode")).Dispose();

        string after =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
        after.Should().Contain("not a file a printer would accept");
        after.Should().Contain("No files yet", "and nothing was stored");

        client.Dispose();
    }

    /// <summary>Posts the upload form the way a browser would: multipart, with the antiforgery field.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification =
                         "MultipartFormDataContent takes ownership of the parts added to it and disposes them with itself, which the using declaration below does.")]
    private static async Task<HttpResponseMessage> PostFileAsync(HttpClient client, string page, string name, string content)
    {
        // Disposing the MultipartFormDataContent disposes the parts it owns, which is why neither the
        // token nor the file content is disposed separately here.
        using MultipartFormDataContent form = [];

        form.Add(new StringContent(AntiforgeryTestHelper.ExtractToken(page)), "__RequestVerificationToken");
        form.Add(new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(content))), "file", name);

        return await client.PostAsync("/Files?handler=Upload", form);
    }

    /// <summary>
    /// A file name somebody chose is the only string on this page with no length anybody promised,
    /// and the cell holding it has to be allowed to break mid-word.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What goes wrong without it is the whole page, not the cell.</b> A long name with no space
    /// in it gives the browser nowhere to wrap, so the column cannot shrink below its own content,
    /// the table grows past the viewport, and everything - navigation included - scrolls sideways.
    /// </para>
    /// <para>
    /// <b>This asserts the class, not the geometry</b>, and that is the honest limit of it: these
    /// tests parse HTML and never lay it out, so nothing here can see an overflow. What it does buy
    /// is a guard on the plumbing - a rewritten row or a new column that drops <c>typed-name</c>
    /// fails here rather than on somebody's phone. The rule itself lives in <c>site.css</c>, with
    /// the reasoning for <c>anywhere</c> over <c>break-word</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ALongFileNameIsAllowedToBreakSoTheTableDoesNot()
    {
        // Arrange
        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "pagelongname@example.com");

        // No space anywhere in it, which is what removes every break opportunity.
        string unbroken = new string('n', 180) + ".gcode";
        await UploadAsync(client, unbroken, 512);

        // Act
        string page =
            await (await client.GetAsync("/Files", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);

        // Assert
        page.Should().Contain(unbroken, "the name is rendered whole rather than cut short");
        page.Should().Contain(
            $"<td class=\"typed-name\">{unbroken}</td>",
            "the cell carrying a name somebody chose has to be allowed to wrap inside a word");

        client.Dispose();
    }

    private static async Task UploadAsync(HttpClient client, string name, int bytes)
    {
        using StreamContent body = new(new MemoryStream(Encoding.UTF8.GetBytes(new string('G', bytes))));
        using HttpResponseMessage response = await client.PutAsync($"/api/v1/files/{name}", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture upload has to have worked");
    }
}
