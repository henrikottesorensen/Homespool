using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

using Homespool.Host.DTO;
using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Host.Queue;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Controllers;

/// <summary>
/// The print-host compatibility shell: the two requests PrusaSlicer makes when it sends a sliced file
/// to a printer. Design and the rejected alternatives: <c>notes/prusa-slicer-print-host.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is an adapter, not API surface.</b> <c>notes/file-storage.md</c> settled that <c>/api/v1</c>
/// is ours and only <c>/p/*</c> owes Prusa anything, while keeping a compatibility shell possible
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
    private readonly PrintFileCatalog _files;
    private readonly PrintQueueService _queue;
    private readonly PrinterQueryService _printers;
    private readonly UserManager<HSUser> _userManager;
    private readonly PrintFileStorageOptions _options;

    public OctoPrintCompatController(PrintFileCatalog files,
                               PrintQueueService queue,
                               PrinterQueryService printers,
                               UserManager<HSUser> userManager,
                               IOptions<PrintFileStorageOptions> options)
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
    public async Task<ActionResult<OctoPrintVersionDTO>> Version(Guid uuid, CancellationToken cancellationToken)
    {
        (Printer? printer, _, ActionResult? failure) = await ResolveAsync(uuid, cancellationToken);

        if (printer is null)
        {
            return failure!;
        }

        return Ok(new OctoPrintVersionDTO());
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
    /// and queue: <c>notes/print-queue.md</c> is emphatic that a person does not start prints - the
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
    [RequestSizeLimit(long.MaxValue)] // Enforced against the configured cap below, not by MVC.

    // Without this MVC buffers the whole body into Request.Form before this method runs, and the
    // MultipartReader below then reads an exhausted stream. See the attribute's own documentation.
    [DisableFormValueModelBinding]
    public async Task<ActionResult> Upload(Guid uuid, CancellationToken cancellationToken)
    {
        // Before a byte of the body is read: with Expect: 100-continue - which libcurl adds to every
        // upload this size - Kestrel withholds the 100 until something reads the body, so refusing
        // here means the file is never transferred at all rather than uploaded and then rejected.
        (Printer? printer, HSUser? caller, ActionResult? failure) = await ResolveAsync(uuid, cancellationToken);

        if (printer is null)
        {
            return failure!;
        }

        // ResolveAsync returns a user whenever it returns a printer - it cannot find one without an
        // owner to scope the lookup by. Asserted once here so nothing below carries a nullable.
        HSUser owner = caller!;

        if (!IsMultipart(Request.ContentType, out string? boundary))
        {
            return this.Failure(StatusCodes.Status400BadRequest,
                "Expected a multipart/form-data body, as a print host receives.");
        }

        MultipartReader reader = new(boundary, Request.Body);
        bool print = false;
        StoredFile? stored = null;

        try
        {
            MultipartSection? section;

            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition,
                                                            out ContentDispositionHeaderValue? disposition))
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
                    // Read into a bounded reader rather than the section stream directly: this is a
                    // four-character flag, and nothing should be able to make it a memory cost.
                    string value = await new StreamReader(section.Body).ReadToEndAsync(cancellationToken);
                    print = string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch (UploadTooLargeException)
        {
            return this.Failure(StatusCodes.Status413PayloadTooLarge,
                $"Larger than the {_options.MaxUploadBytes}-byte limit.");
        }
        catch (PrintFileNameConflictException e)
        {
            // The slicer surfaces this verbatim as "HTTP 409: <body>", and its send dialog lets the
            // user rename without leaving the slicer.
            return this.Failure(StatusCodes.Status409Conflict, e.Message);
        }
        catch (ArgumentException e)
        {
            return this.Failure(StatusCodes.Status400BadRequest, e.Message);
        }

        if (stored is null)
        {
            return this.Failure(StatusCodes.Status400BadRequest, "The request carried no file part.");
        }

        if (print)
        {
            try
            {
                await _queue.EnqueueAsync(printer.Id, owner.Id, stored.FileName, cancellationToken);
            }
            catch (TeamAccessDeniedException)
            {
                // The file is already stored, so this is a partial success: say what happened rather
                // than implying nothing did.
                return this.Failure(StatusCodes.Status403Forbidden,
                    $"'{stored.FileName}' was uploaded, but you may not change this printer's queue.");
            }
        }

        return Created();
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

        return await _files.SaveAsync(owner.Id, fileName, limited, overwrite: false, cancellationToken,
            owner.UserName);
    }

    /// <summary>
    /// Resolves the route's printer and the caller, or the failure to answer with. The same
    /// null-covers-both rule <see cref="PrintQueueController"/> follows: telling "no such printer"
    /// apart from "not yours" would confirm the existence of other people's printers.
    /// </summary>
    private async Task<(Printer? printer, HSUser? user, ActionResult? failure)> ResolveAsync(
        Guid uuid, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            // [Authorize] should make this unreachable; fail closed rather than act on an invented id.
            return (null, null, Forbid());
        }

        Printer? printer = await _printers.GetPrinterForUserAsync(uuid, user.Id, cancellationToken);

        if (printer is null)
        {
            return (null, user, NotFound());
        }

        return (printer, user, null);
    }
}
