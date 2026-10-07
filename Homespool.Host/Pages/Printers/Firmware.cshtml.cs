using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.Firmware;
using Homespool.Host.Localisation;
using Homespool.Host.PrintFiles;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Printers;

/// <summary>
/// One printer's firmware: what it runs, the stored images that fit it, and uploading another.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own page rather than a section of <see cref="DetailModel"/></b>, which refreshes itself in
/// parts on timers; a file input in the middle of that would have its choice replaced under it.
/// </para>
/// <para>
/// <b><see cref="Capability.ManagePrinter"/> on this printer is the whole permission</b>, and
/// <see cref="FirmwareImages"/> is what enforces it. The page asks first only to answer a person who
/// can see the printer but not manage it with a refusal rather than a form that cannot work.
/// </para>
/// </remarks>
[Authorize]
[BoundedUpload(FirmwareStorageOptions.MaxImageBytes)]
public class FirmwareModel : PageModel
{
    private readonly PrinterQueryService _printers;
    private readonly PrinterAccessService _access;
    private readonly FirmwareImages _images;
    private readonly UserManager<HSUser> _userManager;
    private readonly ErrorText _errors;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public FirmwareModel(PrinterQueryService printers,
                         PrinterAccessService access,
                         FirmwareImages images,
                         UserManager<HSUser> userManager,
                         ErrorText errors,
                         IStringLocalizer<SharedResource> localiser)
    {
        _printers = printers;
        _access = access;
        _images = images;
        _userManager = userManager;
        _errors = errors;
        _localiser = localiser;
    }

    /// <summary>The printer, once the page has found it for the caller.</summary>
    public Printer Printer { get; private set; } = null!;

    /// <summary>Every stored image that fits this printer, newest first.</summary>
    public IReadOnlyList<FirmwareImage> Images { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public bool StatusSuccess { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid uuid, CancellationToken cancellationToken)
    {
        return await LoadAsync(uuid, listImages: true, cancellationToken) ?? Page();
    }

    /// <summary>Stores an uploaded image for this printer, or says why not.</summary>
    /// <remarks>
    /// <b>Binds <see cref="IFormFile"/> rather than streaming the body</b>, as the Files page does and
    /// for its reason: antiforgery reads the form before the handler runs. The image is a few
    /// megabytes, and <see cref="BoundedUploadAttribute"/> caps the request.
    /// </remarks>
    public async Task<IActionResult> OnPostUploadAsync(Guid uuid, IFormFile? file, CancellationToken cancellationToken)
    {
        if (await LoadAsync(uuid, listImages: false, cancellationToken) is IActionResult refused)
        {
            return refused;
        }

        if (file is null || file.Length == 0)
        {
            (StatusMessage, StatusSuccess) = (_localiser["Firmware_NoFileChosen"].Value, false);

            return RedirectToPage(new { uuid });
        }

        try
        {
            await using Stream content = file.OpenReadStream();

            FirmwareImage image = await _images.StoreAsync(await CallerAsync(), Printer.Id, file.FileName, content,
                                                           cancellationToken);

            (StatusMessage, StatusSuccess) = (_localiser["Firmware_Stored", image.Name, image.Header.Version].Value, true);
        }
        catch (Exception e) when (e is FirmwareImageRefusedException or PrintFileNameRejectedException)
        {
            (StatusMessage, StatusSuccess) = (_errors.For(e), false);
        }
        catch (UploadTooLargeException)
        {
            (StatusMessage, StatusSuccess) = (_localiser["Firmware_TooLarge",
                                                         ByteSize.Format(FirmwareStorageOptions.MaxImageBytes, _localiser)].Value,
                                              false);
        }

        return RedirectToPage(new { uuid });
    }

    /// <summary>
    /// Finds the printer for the caller, and the images when <paramref name="listImages"/> - each one
    /// is verified again to be listed, which a post has no use for - or the answer to give instead: not
    /// found for a printer they cannot see, forbidden for one they can see but not manage.
    /// </summary>
    private async Task<IActionResult?> LoadAsync(Guid uuid, bool listImages, CancellationToken cancellationToken)
    {
        Caller caller = await CallerAsync();
        Printer? printer = await _printers.GetPrinterForUserAsync(uuid, caller, cancellationToken);

        if (printer is null)
        {
            return NotFound();
        }

        if (!await _access.AllowsAsync(printer.Id, caller, Capability.ManagePrinter, cancellationToken))
        {
            return Forbid();
        }

        Printer = printer;

        if (listImages)
        {
            Images = await _images.ListForAsync(caller, printer.Id, cancellationToken);
        }

        return null;
    }

    private async Task<Caller> CallerAsync()
    {
        HSUser user = await _userManager.GetUserAsync(User) ??
                      throw new InvalidOperationException("An authorised page found no signed-in user.");

        return CallerResolver.For(user, User);
    }
}
