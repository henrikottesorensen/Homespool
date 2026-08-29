using System;
using System.IO;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

using Homespool.Host.Authorisation;
using Homespool.Host.DTO;
using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Host.Queue;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Controllers;

/// <summary>
/// The print-host compatibility shell: the two requests PrusaSlicer makes when it sends a sliced file
/// to a printer.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is an adapter, not API surface.</b> <c>/api/v1</c> is ours and only <c>/p/*</c> owes
/// Prusa anything, while keeping a compatibility shell possible
/// later - gated on a retargetable client appearing. PrusaSlicer is that client, because
/// <c>print_host</c> is a free-text field. The shape here is therefore dictated by the slicer and the
/// translation stops at this class: everything below it is called exactly as the web UI calls it.
/// </para>
/// <para>
/// <b>The printer is in the URL</b> because PrusaSlicer binds one <c>print_host</c> per physical
/// printer, which is already one entry per real machine. A user pastes
/// <c>https://homespool.example/compat/octoprint/{uuid}/</c> into the Host field and the slicer
/// appends <c>api/version</c> and <c>api/files/local</c> itself.
/// </para>
/// <para>
/// <b><c>/compat</c> is a namespace, not decoration.</b> It makes "is this ours, or somebody else's
/// shape?" answerable from the URL alone, which turns three per-controller decisions into one rule
/// with a scope: nothing under it is <c>/api/v1</c>, nothing under it appears in the OpenAPI
/// document, and everything under it takes the token-only policy. A second emulated protocol would
/// be <c>/compat/prusalink/{uuid}</c> and inherit all three. The protocol is spelled out rather than
/// abbreviated because the one moment a human reads this string is while choosing <b>OctoPrint</b>
/// from the slicer's host-type dropdown, and matching that word exactly is the whole job.
/// </para>
/// <para>
/// <b>Hidden from the OpenAPI document on purpose.</b> The document describes this application's API,
/// and these routes are somebody else's client's shape. Publishing them would invite people to script
/// against a compatibility affordance, which is the outcome
/// <see cref="Authentication.XApiKeyAuthenticationHandler"/> is scoped to avoid.
/// </para>
/// </remarks>
[ApiController]
[Route("/compat/octoprint/{uuid:guid}")]
[Authorize(Policy = Authorisation.Policies.Compat)]
[ApiExplorerSettings(IgnoreApi = true)]
public class OctoPrintCompatController : ControllerBase
{
    /// <summary>
    /// What the <c>print</c> part may be. The value is <c>"true"</c> or <c>"false"</c>; a kilobyte is
    /// already three orders of magnitude of headroom, and the cap exists so that one form section
    /// cannot spend the whole request budget on a single string.
    /// </summary>
    private const int PrintFlagMaxBytes = 1024;

    private readonly PrintFileCatalog _files;
    private readonly PrintQueueService _queue;
    private readonly PrinterQueryService _printers;
    private readonly UserManager<HSUser> _userManager;
    private readonly PrintFileStorageOptions _options;

    public OctoPrintCompatController(PrintFileCatalog files,
                                     PrintQueueService queue,
                                     PrinterQueryService printers,
                                     UserManager<HSUser> userManager,
                                     IOptionsSnapshot<PrintFileStorageOptions> options)
    {
        _files = files;
        _queue = queue;
        _printers = printers;
        _userManager = userManager;
        _options = options.Value;
    }

    /// <summary>
    /// The version probe. <c>GET /compat/octoprint/{uuid}/api/version</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issued twice by the slicer: once by the Physical Printer dialog's <b>Test</b> button, and again
    /// unconditionally before every upload, so a send costs this request first
    /// (<c>docs/prusa-slicer-integration.md</c> §2.3). Any non-2xx aborts the upload before a byte of
    /// the file is sent.
    /// </para>
    /// <para>
    /// <b>It 404s for a printer this caller cannot see</b>, rather than answering for the server in
    /// general. That is what makes Test a real test: it says "this URL will accept your print", not
    /// "something is listening".
    /// </para>
    /// </remarks>
    [HttpGet]
    [Route("api/version")]
    public async Task<Results<Ok<OctoPrintVersionDTO>, ForbiddenProblem, NotFoundProblem>> Version(Guid uuid,
                                                                                                  CancellationToken cancellationToken)
    {
        (Printer? printer, HSUser? caller) = await ResolveAsync(uuid, cancellationToken);

        if (caller is null)
        {
            return this.NoAccount();
        }

        if (printer is null)
        {
            return this.NotFoundProblem();
        }

        return TypedResults.Ok(new OctoPrintVersionDTO());
    }

    /// <summary>
    /// The upload. <c>POST /compat/octoprint/{uuid}/api/files/local</c>, <c>multipart/form-data</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Parts, as the slicer builds them: <c>print</c> (<c>"true"</c>/<c>"false"</c>), <c>path</c>, and
    /// <c>file</c> whose part filename is the target name (§2.3). <b><c>path</c> is discarded</b> - it
    /// is the parent directory, sent <em>unescaped</em>, and there are no folders here, so the only
    /// safe thing to do with it is nothing.
    /// </para>
    /// <para>
    /// <b><c>print=true</c> queues rather than prints.</b> "Upload and Print" honestly becomes upload
    /// and queue: a person does not start prints - the
    /// printer's producer loop pulls when the printer is <c>Ready</c> - so a shell that sent
    /// <c>START_PRINT</c> would be racing the loop and reintroducing the shape that design dissolved.
    /// </para>
    /// <para>
    /// <b>An existing name is a 409, exactly as <c>/api/v1</c> answers it</b> (Henrik, 2026-08-05).
    /// The slicer has no overwrite flag to offer, but its send dialog carries an editable filename
    /// field, so a clash is resolved where it happens and one rule holds everywhere.
    /// </para>
    /// </remarks>
    [HttpPost]
    [Route("api/files/local")]

    // Was [RequestSizeLimit(long.MaxValue)], which removed Kestrel's ceiling and put nothing in its
    // place: the file section was bounded by LengthLimitingStream and every other section was not.
    // The configured form, so this endpoint and the Files page move together with one setting.
    [BoundedUpload]

    // Without this MVC buffers the whole body into Request.Form before this method runs, and the
    // MultipartReader below then reads an exhausted stream. See the attribute's own documentation.
    [DisableFormValueModelBinding]
    public async Task<Results<Created, ContentHttpResult, ForbiddenProblem, NotFoundProblem>> Upload(Guid uuid,
                                                                                                    CancellationToken cancellationToken)
    {
        // Before a byte of the body is read: with Expect: 100-continue - which libcurl adds to every
        // upload this size - Kestrel withholds the 100 until something reads the body, so refusing
        // here means the file is never transferred at all rather than uploaded and then rejected.
        (Printer? printer, HSUser? owner) = await ResolveAsync(uuid, cancellationToken);

        if (owner is null)
        {
            return this.NoAccount();
        }

        if (printer is null)
        {
            return this.NotFoundProblem();
        }

        if (!IsMultipart(Request.ContentType, out string? boundary))
        {
            return Explain(StatusCodes.Status400BadRequest,
                           "Expected a multipart/form-data body, as a print host receives.");
        }

        MultipartReader reader = new(boundary, Request.Body);
        bool print = false;
        StoredFile? stored = null;

        // Held outside the loop so a failure can name the file it was reading.
        ContentDispositionHeaderValue? disposition = null;

        try
        {
            MultipartSection? section;

            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out disposition))
                {
                    continue;
                }

                if (disposition.IsFileDisposition())
                {
                    stored = await ReadFileSectionAsync(section, disposition, owner, cancellationToken);
                }
                else if (disposition.IsFormDisposition()
                         && string.Equals(disposition.Name.Value, "print", StringComparison.OrdinalIgnoreCase))
                {
                    print = await ReadPrintFlagAsync(section, cancellationToken);
                }
            }
        }
        catch (UploadTooLargeException)
        {
            return Explain(StatusCodes.Status413PayloadTooLarge,
                           $"That file is larger than this server's {_options.MaxUploadBytes}-byte upload limit.");
        }
        catch (PrintFileNameConflictException)
        {
            // Deliberately not the store's own message, which advises asking to overwrite - correct
            // for /api/v1, where the caller holds that flag, and useless here, where the caller is a
            // slicer that has none. Name the action this caller actually has instead.
            return Explain(StatusCodes.Status409Conflict,
                           $"A file named '{Path.GetFileName(disposition?.FileName.Value ?? string.Empty)}' already exists. "
                           + "Rename it in the send dialog, or delete the existing file in Homespool.");
        }
        catch (ArgumentException e)
        {
            return Explain(StatusCodes.Status400BadRequest, e.Message);
        }

        if (stored is null)
        {
            return Explain(StatusCodes.Status400BadRequest, "The request carried no file part.");
        }

        if (print)
        {
            try
            {
                // The findings are dropped here on purpose: this is the endpoint PrusaSlicer posts to,
                // whose response shape is not ours to extend and whose caller is a slicer rather than a
                // reader. A holding finding still stops the print at the loop, and the person sees it on
                // the printer's page - which is where they would look after pressing Send anyway.
                await _queue.EnqueueAsync(printer.Id, CallerResolver.For(owner, User), stored.FileName, cancellationToken);
            }
            catch (TeamAccessDeniedException)
            {
                // The file is already stored, so this is a partial success: say what happened rather
                // than implying nothing did.
                return Explain(StatusCodes.Status403Forbidden,
                               $"'{stored.FileName}' was uploaded, but you may not change this printer's queue.");
            }
        }

        return TypedResults.Created();
    }

    /// <summary>
    /// A failure the slicer can put in front of a person: status plus one plain sentence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately not <c>ProblemDetails</c>, which every other controller here answers with.</b>
    /// <c>PrintHost::format_error</c> composes <c>"HTTP &lt;status&gt;: &lt;body&gt;"</c> and shows it
    /// verbatim in a dialog, so a JSON document arrives at a human with the one useful sentence buried
    /// between a spec URL and a trace id - observed on a live send, 2026-08-06. A machine-readable
    /// body is the right convention for <c>/api/v1</c>, whose callers parse it; this surface's only
    /// client renders it.
    /// </para>
    /// <para>
    /// It also keeps the <c>traceId</c> out of a stranger's error dialog, which is small but not
    /// nothing.
    /// </para>
    /// </remarks>
    private static ContentHttpResult Explain(int status, string message)
    {
        return TypedResults.Text(message, $"{MediaTypeNames.Text.Plain}; charset=utf-8", statusCode: status);
    }

    /// <summary>
    /// Reads the boundary out of a <c>multipart/form-data</c> content type, or false if this is not
    /// one.
    /// </summary>
    private static bool IsMultipart(string? contentType, out string boundary)
    {
        boundary = string.Empty;

        if (contentType is null
            || !MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? parsed)
            || !parsed.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? value = HeaderUtilities.RemoveQuotes(parsed.Boundary).Value;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        boundary = value;

        return true;
    }

    /// <summary>
    /// The <c>print</c> flag, bounded before it is read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bound is the point.</b> The flag is <c>"true"</c> or <c>"false"</c>, but
    /// <see cref="StreamReader.ReadToEndAsync(CancellationToken)"/> accumulates whatever arrives into
    /// one string, and a <c>StreamReader</c>'s <c>bufferSize</c> is how much it reads at a time rather
    /// than how much it will hold. So the section needs a limit of its own: the request ceiling bounds
    /// the body, and without this one part could spend all of it on a single managed string.
    /// </para>
    /// <para>
    /// <b>Refused as malformed rather than as too large.</b> A <c>print</c> part over the cap is not a
    /// large flag, it is not a flag - so this answers 400 through <see cref="ArgumentException"/>
    /// rather than the 413 that names a file and the upload limit, neither of which is what went
    /// wrong. Other form parts are never read at all; <c>ReadNextSectionAsync</c> drains them, so they
    /// cost bandwidth and not memory.
    /// </para>
    /// </remarks>
    private static async Task<bool> ReadPrintFlagAsync(MultipartSection section, CancellationToken cancellationToken)
    {
        try
        {
            await using LengthLimitingStream bounded = new(section.Body, PrintFlagMaxBytes);

            // leaveOpen: the section's stream belongs to the multipart reader, which closes it.
            using StreamReader flag = new(bounded, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                                          bufferSize: PrintFlagMaxBytes, leaveOpen: true);

            string value = await flag.ReadToEndAsync(cancellationToken);

            return string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (UploadTooLargeException)
        {
            throw new ArgumentException(
                $"The 'print' part is larger than {PrintFlagMaxBytes} bytes, so it is not a flag.");
        }
    }

    /// <summary>
    /// Streams one file part into storage under the name the slicer gave it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Streamed, never buffered.</b> This is why the sections are read by hand instead of bound to
    /// an <c>IFormFile</c>: that path buffers, and its <c>MultipartBodyLengthLimit</c> defaults to
    /// 128 MB, which would fight the configured cap rather than enforce it.
    /// </para>
    /// <para>
    /// <see cref="Path.GetFileName(string)"/> strips any directory the part filename carries, so the
    /// name faces the same allowlist and the same per-user tree as an <c>/api/v1</c> upload.
    /// </para>
    /// </remarks>
    private async Task<StoredFile> ReadFileSectionAsync(MultipartSection section,
                                                        ContentDispositionHeaderValue disposition,
                                                        HSUser owner,
                                                        CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(HeaderUtilities.RemoveQuotes(disposition.FileName).Value ?? string.Empty);

        if (!UserFileStore.IsAllowedExtension(fileName))
        {
            throw new ArgumentException(
                "Only .gcode, .bgcode, .gco and .bgc are accepted - the printer would refuse anything "
                + "else after the transfer.");
        }

        await using LengthLimitingStream limited = new(section.Body, _options.MaxUploadBytes);

        return await _files.SaveAsync(CallerResolver.For(owner, User), fileName, limited, overwrite: false, cancellationToken,
                                      owner.UserName);
    }

    /// <summary>
    /// Resolves the route's printer and the caller - either null being a refusal the action answers
    /// itself, the caller first. The same null-covers-both rule <see cref="PrintQueueController"/>
    /// follows: telling "no such printer" apart from "not yours" would confirm the existence of other
    /// people's printers.
    /// </summary>
    private async Task<(Printer? printer, HSUser? user)> ResolveAsync(Guid uuid, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return (null, null);
        }

        Printer? printer = await _printers.GetPrinterForUserAsync(uuid, CallerResolver.For(user, User), cancellationToken);

        return (printer, user);
    }
}
