using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.Pages;
using Homespool.Host.PrintFiles;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Firmware;

/// <summary>
/// The firmware store and its rows, kept in step: storing an image checks it, writes it to disk and
/// indexes it as a <see cref="FileType.PrusaFirmware"/> file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared, and only ever Prusa's.</b> Any printer manager may use any stored image on a printer
/// they manage, because nothing gets in that the verifier did not find intact and signed by Prusa:
/// whoever uploaded it, it is the same official bytes. The row's owner is the uploader, which is true
/// and is all it means.
/// </para>
/// <para>
/// <b>Kept by digest, named by upload.</b> The bytes live at <c>{root}/{digest}.bbf</c>, so the same
/// image uploaded twice is one file and one row, and no name a person chose ever becomes a path here.
/// The name they uploaded it under is the row's, and is what the image is called on a printer's drive.
/// </para>
/// <para>
/// <b>The disk is checked again on every read.</b> Listing re-runs the verifier over each image, so a
/// file changed underneath this - by hand, or by a disk going bad - stops being offered rather than
/// being believed because a row once said it was good. Prusa's images are a few megabytes and a
/// deployment holds a handful.
/// </para>
/// <para>
/// <b>The permission is <see cref="Capability.ManagePrinter"/> on the printer in hand</b>, checked
/// here rather than by the page, as every printer rule is: storing an image happens on a printer's
/// behalf, and is refused for one that does not fit it.
/// </para>
/// </remarks>
public sealed class FirmwareImages
{
    private const string IncomingDirectory = ".incoming";
    private const string Extension = ".bbf";

    private readonly PrinterAccessService _access;
    private readonly HomespoolDbContext _dbContext;
    private readonly PrusaFirmwareVerifier _verifier;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FirmwareImages> _logger;
    private readonly string _root;

    public FirmwareImages(PrinterAccessService access,
                          HomespoolDbContext dbContext,
                          PrusaFirmwareVerifier verifier,
                          IOptionsMonitor<FirmwareStorageOptions> options,
                          IHostEnvironmentAccessor environment,
                          TimeProvider timeProvider,
                          ILogger<FirmwareImages> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        _access = access;
        _dbContext = dbContext;
        _verifier = verifier;
        _timeProvider = timeProvider;
        _logger = logger;
        _root = Path.IsPathRooted(options.CurrentValue.Directory) ?
            options.CurrentValue.Directory :
            Path.Combine(environment.ContentRootPath, options.CurrentValue.Directory);
    }

    /// <summary>
    /// Checks an upload and stores it as a firmware image for <paramref name="printerId"/>, or answers
    /// with the image already stored under the same bytes.
    /// </summary>
    /// <param name="caller">Who is uploading; needs <see cref="Capability.ManagePrinter"/> on the printer.</param>
    /// <param name="printerId">The printer the image is uploaded for, which it must fit.</param>
    /// <param name="fileName">The name it was uploaded under.</param>
    /// <param name="content">The bytes, read to their end.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <exception cref="FirmwareImageRefusedException">The upload is not an image this printer may be given.</exception>
    /// <exception cref="PrintFileNameRejectedException">The name is not one a printer's drive can hold.</exception>
    /// <exception cref="UploadTooLargeException">The upload ran past <see cref="FirmwareStorageOptions.MaxImageBytes"/>.</exception>
    /// <exception cref="TeamAccessDeniedException">The caller may not manage this printer.</exception>
    public async Task<FirmwareImage> StoreAsync(Caller caller,
                                                int printerId,
                                                string fileName,
                                                Stream content,
                                                CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(content);

        Printer printer = await _access.RequireAsync(printerId, caller, Capability.ManagePrinter, cancellationToken);
        string name = UserFileStore.RequireSafeName(fileName);

        if (!name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new FirmwareImageRefusedException(name, FirmwareImageRefusal.NotAnImageName);
        }

        string incoming = Path.Combine(_root, IncomingDirectory);
        Directory.CreateDirectory(incoming);
        string staged = Path.Combine(incoming, Guid.NewGuid().ToString("N"));

        try
        {
            string digest;

            await using (FileStream target = new(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await using LengthLimitingStream limited = new(content, FirmwareStorageOptions.MaxImageBytes);
                digest = await PrintFileDigest.ComputeAsync(limited, target, cancellationToken);
            }

            PrusaFirmwareCheck check = await CheckAsync(staged, cancellationToken);

            if (!check.IsVerified)
            {
                throw new FirmwareImageRefusedException(name, FirmwareImageRefusal.NotVerified, check);
            }

            string printerName = PrinterDisplayName.For(printer);

            if (printer.Model is null)
            {
                throw new FirmwareImageRefusedException(name, FirmwareImageRefusal.PrinterModelUnknown, check, printerName);
            }

            if (!PrusaFirmwareCompatibility.Fits(check.Header!, printer.Model))
            {
                throw new FirmwareImageRefusedException(name, FirmwareImageRefusal.WrongPrinter, check, printerName);
            }

            HSFile? existing = await _dbContext.Files
                                               .FirstOrDefaultAsync(row => row.Type == FileType.PrusaFirmware &&
                                                                           row.Digest == digest,
                                                                    cancellationToken);

            if (existing is not null)
            {
                // The same bytes again. Put them back if the file went missing under the row, which
                // makes uploading again the remedy for an image that stopped being offered.
                if (!File.Exists(PathFor(digest)))
                {
                    File.Move(staged, PathFor(digest));
                }

                return Describe(existing, check.Header!);
            }

            if (await _dbContext.Files.AnyAsync(row => row.Type == FileType.PrusaFirmware &&
                                                       row.UserId == caller.UserId &&
                                                       row.Name == name,
                                                cancellationToken))
            {
                throw new FirmwareImageRefusedException(name, FirmwareImageRefusal.NameTaken, check);
            }

            long size = new FileInfo(staged).Length;
            File.Move(staged, PathFor(digest), overwrite: true);

            HSFile row = new()
            {
                Type = FileType.PrusaFirmware,
                UserId = caller.UserId,
                Name = name,
                Size = size,
                Digest = digest,
                UploadedAt = _timeProvider.GetUtcNow(),

                // Readable, and says nothing about slicing - which is what this state means.
                MetadataState = PrintFileMetadataState.Silent,
            };

            _dbContext.Files.Add(row);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Stored firmware {Version} for printer type {Build} as {FileName}, uploaded by user {UserId}",
                                   check.Header!.Version, PrusaFirmwareCompatibility.BuildOf(check.Header), name, caller.UserId);

            return Describe(row, check.Header);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    /// <summary>Every stored image that is still intact and fits <paramref name="printerId"/>, newest first.</summary>
    /// <exception cref="TeamAccessDeniedException">The caller may not manage this printer.</exception>
    public async Task<IReadOnlyList<FirmwareImage>> ListForAsync(Caller caller,
                                                                 int printerId,
                                                                 CancellationToken cancellationToken)
    {
        Printer printer = await _access.RequireAsync(printerId, caller, Capability.ManagePrinter, cancellationToken);

        List<HSFile> rows = await _dbContext.Files
                                            .AsNoTracking()
                                            .Where(row => row.Type == FileType.PrusaFirmware)
                                            .ToListAsync(cancellationToken);

        List<FirmwareImage> images = [];

        foreach (HSFile row in rows.OrderByDescending(row => row.UploadedAt))
        {
            PrusaFirmwareCheck? check = row.Digest is null ? null : await CheckAsync(PathFor(row.Digest), cancellationToken);

            if (check?.IsVerified != true)
            {
                _logger.LogWarning("Firmware image {FileName} no longer verifies ({Verdict}); not offering it",
                                   row.Name, check?.Verdict);

                continue;
            }

            if (PrusaFirmwareCompatibility.Fits(check.Header!, printer.Model))
            {
                images.Add(Describe(row, check.Header!));
            }
        }

        return images;
    }

    private static FirmwareImage Describe(HSFile row, PrusaFirmwareHeader header)
    {
        return new FirmwareImage(row.Digest!, row.Name, header, row.Size, row.UploadedAt);
    }

    private string PathFor(string digest)
    {
        return Path.Combine(_root, digest + Extension);
    }

    /// <summary>Checks the file at <paramref name="path"/>, or reports a missing one as no image at all.</summary>
    private async Task<PrusaFirmwareCheck> CheckAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            return await _verifier.CheckAsync(stream, cancellationToken);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new PrusaFirmwareCheck(PrusaFirmwareVerdict.NotAnImage, Header: null);
        }
    }
}
