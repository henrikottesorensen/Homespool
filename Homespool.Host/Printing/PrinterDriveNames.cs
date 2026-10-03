using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Queue;
using Homespool.Model.Entities;

namespace Homespool.Host.Printing;

/// <summary>
/// Which name a file is stored under on a printer's drive, recorded so that every transfer Homespool
/// makes leaves a row the next one can see.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place for the queue and both direct sends.</b> All three put files in the one <c>/usb/</c>
/// of a printer. A transfer that left no record was invisible to the others' naming, so a later
/// transfer of another user's file of the same name found the name taken by bytes it could not tell
/// from its own - and, at the same size, adopted them. Choosing and recording the name here means
/// what sits on a drive under a name Homespool chose can always be attributed.
/// </para>
/// <para>
/// <b>A reservation, not a transfer the queue waits on.</b> The row a direct send leaves carries a drive
/// name and, once the printer takes the file, the command id its ending will name - but no
/// <see cref="PrintFileOnPrinter.TransferStartedAt"/>, which is the queue's mark on an attempt of its
/// own: one it waits on before sending again, and whose failure it counts. The name is what the next
/// transfer needs to see.
/// </para>
/// </remarks>
public sealed class PrinterDriveNames
{
    private readonly HomespoolDbContext _dbContext;

    public PrinterDriveNames(HomespoolDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>Where on a printer's drive a file stored under <paramref name="driveName"/> is.</summary>
    public static string OnDrive(string driveName)
    {
        return $"/usb/{driveName}";
    }

    /// <summary>
    /// The first name for <paramref name="file"/> that no other file on this printer is known by.
    /// </summary>
    /// <remarks>
    /// Its own name when that is free, which is every transfer that shares a printer with nobody.
    /// When every name is taken it is still its own, and the printer's refusal decides from there.
    /// </remarks>
    public async Task<string> FirstAsync(int printerId, PrintFile file, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        List<string> taken = await OthersAsync(printerId, file.Id, cancellationToken);
        string? owner = await OwnerNameAsync(file, cancellationToken);

        return DriveNames.First(fileName, owner, name => taken.Exists(other => DriveNames.Same(other, name))) ?? fileName;
    }

    /// <summary>The name after <paramref name="refused"/> for <paramref name="file"/>, or null when none is left.</summary>
    public async Task<string?> AfterAsync(int printerId,
                                          PrintFile file,
                                          string refused,
                                          string fileName,
                                          CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        List<string> taken = await OthersAsync(printerId, file.Id, cancellationToken);
        string? owner = await OwnerNameAsync(file, cancellationToken);

        return DriveNames.After(refused, fileName, owner, name => taken.Exists(other => DriveNames.Same(other, name)));
    }

    /// <summary>
    /// The names every other file with a row on this printer is known by there - sent, reserved or
    /// waiting to be, under its own name or with its owner's.
    /// </summary>
    private Task<List<string>> OthersAsync(int printerId, long printFileId, CancellationToken cancellationToken)
    {
        return _dbContext.PrintFilesOnPrinters
                         .Where(row => row.PrinterId == printerId && row.PrintFileId != printFileId)
                         .Select(row => row.DriveName ?? row.PrintFile!.Name)
                         .ToListAsync(cancellationToken);
    }

    /// <summary>The username of whoever owns <paramref name="file"/>, which is what a second name carries.</summary>
    private Task<string?> OwnerNameAsync(PrintFile file, CancellationToken cancellationToken)
    {
        return _dbContext.Users
                         .Where(user => user.Id == file.UserId)
                         .Select(user => user.UserName)
                         .SingleOrDefaultAsync(cancellationToken);
    }
}
