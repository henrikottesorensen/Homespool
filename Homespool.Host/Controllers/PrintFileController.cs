using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using Homespool.Host.Authorisation;
using Homespool.Host.DTO;
using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Model.Entities;

namespace Homespool.Host.Controllers;

/// <summary>
/// A user's print files: list, upload, download, rename, delete.
/// </summary>
/// <remarks>
/// <para>
/// <b>Files are owned by the user, not by a printer</b>, which is why
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
/// with a personal access token.
/// </para>
/// </remarks>
[ApiController]
[Route("/api/v1/files")]
[Authorize(Policy = Authorisation.Policies.Api)]

// 401 is the auth policy's, not any action's - an unauthenticated caller never reaches one.
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public class PrintFileController : ControllerBase
{
    private readonly PrintFileCatalog _files;
    private readonly UserManager<HSUser> _userManager;
    private readonly PrintFileStorageOptions _options;

    public PrintFileController(PrintFileCatalog files, UserManager<HSUser> userManager, IOptions<PrintFileStorageOptions> options)
    {
        _files = files;
        _userManager = userManager;
        _options = options.Value;
    }

    /// <summary>Everything the caller has uploaded. <c>GET /api/v1/files</c>.</summary>
    [HttpGet]
    public async Task<Results<Ok<IReadOnlyList<PrintFileReadDTO>>, ForbiddenProblem>> List()
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return this.NoAccount();
        }

        return TypedResults.Ok<IReadOnlyList<PrintFileReadDTO>>(
            _files.List(CallerResolver.For(user, User)).Select(PrintFileReadDTO.FromStored).ToList());
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
    public async Task<Results<Ok<PrintFileReadDTO>, BadRequestProblem, ForbiddenProblem, ConflictProblem, PayloadTooLargeProblem>> Upload(
        string fileName,
        [FromQuery] bool overwrite,
        CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return this.NoAccount();
        }

        if (!UserFileStore.IsAllowedExtension(fileName))
        {
            return this.BadRequestProblem("Only .gcode, .bgcode, .gco and .bgc are accepted - the printer would "
                                          + "refuse anything else after the transfer.");
        }

        // Content-Length is advisory (a client may omit it), so this rejects the obvious case early
        // and the copy below is still bounded.
        if (Request.ContentLength > _options.MaxUploadBytes)
        {
            return this.PayloadTooLargeProblem($"Larger than the {_options.MaxUploadBytes}-byte limit.");
        }

        StoredFile stored;

        try
        {
            await using LengthLimitingStream limited = new(Request.Body, _options.MaxUploadBytes);
            stored = await _files.SaveAsync(CallerResolver.For(user, User), fileName, limited, overwrite, cancellationToken,
                                            user.UserName);
        }
        catch (UploadTooLargeException)
        {
            return this.PayloadTooLargeProblem($"Larger than the {_options.MaxUploadBytes}-byte limit.");
        }
        catch (PrintFileNameConflictException e)
        {
            return this.ConflictProblem(e.Message);
        }
        catch (ArgumentException e)
        {
            return this.BadRequestProblem(e.Message);
        }

        return TypedResults.Ok(PrintFileReadDTO.FromStored(stored));
    }

    /// <summary>Downloads a file back. <c>GET /api/v1/files/{fileName}</c>.</summary>
    [HttpGet]
    [Route("{fileName}")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification =
                         "Ownership passes to FileStreamHttpResult, which disposes the stream once the response has been written. Disposing it here would close it before a byte is sent.")]

    // The 200 is said here because a file result carries no metadata of its own; the failures say
    // theirs through the union.
    [Produces(MediaTypeNames.Application.Octet)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<Results<FileStreamHttpResult, ForbiddenProblem, NotFoundProblem>> Download(string fileName)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return this.NoAccount();
        }

        StoredFile? file = _files.Find(CallerResolver.For(user, User), fileName);

        if (file is null)
        {
            return this.NotFoundProblem();
        }

        // Opened before the response starts so a file deleted mid-download still completes: the
        // handle keeps the bytes alive, which is the same property a transfer in flight relies on.
        FileStream content = new(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                 bufferSize: 64 * 1024, useAsync: true);

        return TypedResults.File(content, MediaTypeNames.Application.Octet, file.FileName);
    }

    /// <summary>Renames a file. <c>PATCH /api/v1/files/{fileName}</c>.</summary>
    /// <remarks>
    /// Possible only because the name is the identity rather than a label on content-addressed
    /// bytes. The new name faces the same allowlist as an upload - otherwise renaming would be the
    /// way around it.
    /// </remarks>
    [HttpPatch]
    [Route("{fileName}")]
    public async Task<Results<Ok<PrintFileReadDTO>, BadRequestProblem, ForbiddenProblem, NotFoundProblem, ConflictProblem>> Rename(
        string fileName,
        [FromBody] PrintFileRenameRequest body,
        CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return this.NoAccount();
        }

        try
        {
            StoredFile? renamed = await _files.RenameAsync(CallerResolver.For(user, User), fileName, body.Name, cancellationToken);

            if (renamed is null)
            {
                return this.NotFoundProblem();
            }

            return TypedResults.Ok(PrintFileReadDTO.FromStored(renamed));
        }
        catch (PrintFileNameConflictException e)
        {
            return this.ConflictProblem(e.Message);
        }
        catch (ArgumentException e)
        {
            return this.BadRequestProblem(e.Message);
        }
    }

    /// <summary>Deletes a file. <c>DELETE /api/v1/files/{fileName}</c>.</summary>
    /// <remarks>
    /// The lifecycle's third verb. A transfer already reading these bytes finishes unharmed - its
    /// handle outlives the name.
    /// <para>
    /// <b>409 when a queued print still wants the file.</b> Deleting it would silently cancel that
    /// print - possibly somebody else's, since a printer's queue is shared - so the queue entry has to
    /// go first. See <see cref="PrintFileCatalog.DeleteAsync"/>.
    /// </para>
    /// </remarks>
    [HttpDelete]
    [Route("{fileName}")]
    public async Task<Results<NoContent, ForbiddenProblem, NotFoundProblem, ConflictProblem>> Delete(
        string fileName,
        CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return this.NoAccount();
        }

        return await _files.DeleteAsync(CallerResolver.For(user, User), fileName, cancellationToken) switch
        {
            PrintFileDeletion.Deleted => TypedResults.NoContent(),
            PrintFileDeletion.Queued => this.ConflictProblem($"{fileName} is queued to print. Cancel the queued print first."),
            _ => this.NotFoundProblem(),
        };
    }
}

/// <summary>Body of a rename.</summary>
public class PrintFileRenameRequest
{
    /// <summary>The new name. Faces the same allowlist an upload does.</summary>
    public required string Name { get; set; }
}
