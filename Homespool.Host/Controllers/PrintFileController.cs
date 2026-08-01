using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.DTO;
using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Model.Entities;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Controllers;

/// <summary>
/// A user's print files: list, upload, download, rename, delete.
/// </summary>
/// <remarks>
/// <para>
/// <b>Files are owned by the user, not by a printer</b> (<c>notes/file-storage.md</c>), which is why
/// this is its own controller rather than more routes on <see cref="PrinterController"/>. An upload
/// names no printer; choosing one happens later, and is that controller's business.
/// </para>
/// <para>
/// <b>The vocabulary here is ours.</b> Until 2026-07-31 the app API deliberately borrowed Connect's
/// shapes - a <c>hash</c> to address a file, <c>start/cloud</c> versus <c>start/files</c>. Henrik
/// retired that: their app cannot be pointed at another server, so a familiar shape was never going
/// to buy a working client. Files are addressed by name, which is what the user calls them. A
/// compatibility shell remains possible later as an adapter over this, if a retargetable client ever
/// appears.
/// </para>
/// <para>
/// Cookie- or token-authenticated like the rest of <c>/api/v1</c>, so every call here is curl-able
/// with a personal access token (<c>notes/api-tokens.md</c>).
/// </para>
/// </remarks>
[ApiController]
[Route("/api/v1/files")]
[Authorize(Policy = Authorisation.Policies.Api)]

// 401 is the auth policy's, not any action's - an unauthenticated caller never reaches one.
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public class PrintFileController : ControllerBase
{
    private readonly UserFileStore _files;
    private readonly UserManager<HSUser> _userManager;
    private readonly PrintFileStorageOptions _options;

    public PrintFileController(UserFileStore files, UserManager<HSUser> userManager, IOptions<PrintFileStorageOptions> options)
    {
        _files = files;
        _userManager = userManager;
        _options = options.Value;
    }

    /// <summary>Everything the caller has uploaded. <c>GET /api/v1/files</c>.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PrintFileReadDTO>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<PrintFileReadDTO>>> List()
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return Forbid();
        }

        return Ok(_files.List(user.Id).Select(PrintFileReadDTO.FromStored).ToList());
    }

    /// <summary>
    /// Uploads a file, raw. <c>PUT /api/v1/files/{fileName}</c> with the bytes as the body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raw body rather than multipart: it streams straight to disk, where multipart would buffer a
    /// few hundred megabytes first, and it is trivially curl-able -
    /// <c>curl -T model.bgcode .../api/v1/files/model.bgcode</c>.
    /// </para>
    /// <para>
    /// <b>An existing name is a 409 unless <c>overwrite=true</c> is passed.</b> A re-slice
    /// legitimately produces the same name with new content, but so does an accident, and only one
    /// of them should be silent - so the caller says which it is. Overwriting replaces the content
    /// atomically; anything already reading the old bytes, such as a transfer in flight, keeps
    /// reading them.
    /// </para>
    /// </remarks>
    [HttpPut]
    [Route("{fileName}")]
    [RequestSizeLimit(long.MaxValue)] // Enforced against the configured cap below, not by MVC.
    [ProducesResponseType<PrintFileReadDTO>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<PrintFileReadDTO>> Upload(string fileName,
                                            [FromQuery] bool overwrite,
                                            CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return Forbid();
        }

        if (!UserFileStore.IsAllowedExtension(fileName))
        {
            return this.Failure(StatusCodes.Status400BadRequest,
                "Only .gcode, .bgcode, .gco and .bgc are accepted - the printer would "
                + "refuse anything else after the transfer.");
        }

        // Content-Length is advisory (a client may omit it), so this rejects the obvious case early
        // and the copy below is still bounded.
        if (Request.ContentLength > _options.MaxUploadBytes)
        {
            return this.Failure(StatusCodes.Status413PayloadTooLarge,
                $"Larger than the {_options.MaxUploadBytes}-byte limit.");
        }

        StoredFile stored;

        try
        {
            await using LengthLimitingStream limited = new(Request.Body, _options.MaxUploadBytes);
            stored = await _files.SaveAsync(user.Id, fileName, limited, overwrite, cancellationToken);
        }
        catch (UploadTooLargeException)
        {
            return this.Failure(StatusCodes.Status413PayloadTooLarge,
                $"Larger than the {_options.MaxUploadBytes}-byte limit.");
        }
        catch (PrintFileNameConflictException e)
        {
            return this.Failure(StatusCodes.Status409Conflict, e.Message);
        }
        catch (ArgumentException e)
        {
            return this.Failure(StatusCodes.Status400BadRequest, e.Message);
        }

        return Ok(PrintFileReadDTO.FromStored(stored));
    }

    /// <summary>Downloads a file back. <c>GET /api/v1/files/{fileName}</c>.</summary>
    [HttpGet]
    [Route("{fileName}")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification = "Ownership passes to FileStreamResult, which disposes the stream once the response has been written. Disposing it here would close it before a byte is sent.")]
    [Produces("application/octet-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Download(string fileName)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return Forbid();
        }

        StoredFile? file = _files.Find(user.Id, fileName);

        if (file is null)
        {
            return NotFound();
        }

        // Opened before the response starts so a file deleted mid-download still completes: the
        // handle keeps the bytes alive, which is the same property a transfer in flight relies on.
        FileStream content = new(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);

        return File(content, "application/octet-stream", file.FileName);
    }

    /// <summary>Renames a file. <c>PATCH /api/v1/files/{fileName}</c>.</summary>
    /// <remarks>
    /// Possible only because the name is the identity rather than a label on content-addressed
    /// bytes. The new name faces the same allowlist as an upload - otherwise renaming would be the
    /// way around it.
    /// </remarks>
    [HttpPatch]
    [Route("{fileName}")]
    [ProducesResponseType<PrintFileReadDTO>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PrintFileReadDTO>> Rename(string fileName, [FromBody] PrintFileRenameRequest body)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return Forbid();
        }

        try
        {
            StoredFile? renamed = _files.Rename(user.Id, fileName, body.Name);

            return renamed is null ? NotFound() : Ok(PrintFileReadDTO.FromStored(renamed));
        }
        catch (PrintFileNameConflictException e)
        {
            return this.Failure(StatusCodes.Status409Conflict, e.Message);
        }
        catch (ArgumentException e)
        {
            return this.Failure(StatusCodes.Status400BadRequest, e.Message);
        }
    }

    /// <summary>Deletes a file. <c>DELETE /api/v1/files/{fileName}</c>.</summary>
    /// <remarks>
    /// The lifecycle's third verb. A transfer already reading these bytes finishes unharmed - its
    /// handle outlives the name.
    /// </remarks>
    [HttpDelete]
    [Route("{fileName}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Delete(string fileName)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return Forbid();
        }

        return _files.Delete(user.Id, fileName) ? NoContent() : NotFound();
    }
}

/// <summary>Body of a rename.</summary>
public class PrintFileRenameRequest
{
    /// <summary>The new name. Faces the same allowlist an upload does.</summary>
    public required string Name { get; set; }
}
