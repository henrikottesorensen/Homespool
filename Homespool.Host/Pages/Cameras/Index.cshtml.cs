using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

using Homespool.Host.Authorisation;
using Homespool.Host.Cameras;
using Homespool.Host.Localisation;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Cameras;

/// <summary>
/// Adding, changing and removing cameras.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two ways to add one, because there are two kinds.</b> A network camera is an address somebody
/// types; an attached camera is chosen from a list of what is plugged into this machine. Keeping
/// them as separate forms means neither is a mode of the other, and the attached form can be absent
/// entirely when there is nothing to attach - the same shape the Files page uses for its printer
/// select.
/// </para>
/// <para>
/// <b>The permission split is enforced in <see cref="CameraService"/>, not here.</b> This page hides
/// what a person cannot do, which is courtesy rather than security: the check that matters is the
/// one they cannot route around.
/// </para>
/// </remarks>
[Authorize]
public class IndexModel : PageModel
{
    private readonly CameraService _cameras;
    private readonly CameraAccessService _access;
    private readonly Go2RtcClient _streamServer;
    private readonly CameraFrameCache _frames;
    private readonly PrinterQueryService _printers;
    private readonly CameraDisplayNames _names;
    private readonly UserManager<HSUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public IndexModel(CameraService cameras,
                      CameraAccessService access,
                      Go2RtcClient streamServer,
                      CameraFrameCache frames,
                      PrinterQueryService printers,
                      CameraDisplayNames names,
                      UserManager<HSUser> userManager,
                      IStringLocalizer<SharedResource> localiser)
    {
        _cameras = cameras;
        _access = access;
        _streamServer = streamServer;
        _frames = frames;
        _printers = printers;
        _names = names;
        _userManager = userManager;
        _localiser = localiser;
    }

    /// <summary>The cameras this account may see.</summary>
    public IReadOnlyList<Camera> Cameras { get; private set; } = [];

    /// <summary>Devices plugged into this machine that nobody has claimed.</summary>
    /// <remarks>Always empty for a non-administrator, since they could not claim one anyway.</remarks>
    public IReadOnlyList<LocalCameraDevice> AvailableDevices { get; private set; } = [];

    /// <summary>Teams this account may add a camera to.</summary>
    public IReadOnlyList<Team> Teams { get; private set; } = [];

    /// <summary>Printers a camera can be pointed at.</summary>
    public IReadOnlyList<Printer> Printers { get; private set; } = [];

    /// <summary>Whether this account may claim a camera attached to the server.</summary>
    public bool IsAdministrator { get; private set; }

    /// <summary>The camera being edited, if the query string names one.</summary>
    public Camera? Editing { get; private set; }

    [TempData]
    public string? Message { get; set; }

    [TempData]
    public string? Warning { get; set; }

    [TempData]
    public string? Error { get; set; }

    /// <summary>Whether this camera reads hardware attached to the server.</summary>
    public static bool IsAttached(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);

        return CameraSourcePolicy.IsLocalDevice(camera.Source);
    }

    /// <summary>What to call a camera — see <see cref="CameraDisplayNames"/>.</summary>
    public string DisplayName(Camera camera)
    {
        return _names.For(camera);
    }

    public async Task<IActionResult> OnGetAsync(Guid? edit, CancellationToken cancellationToken)
    {
        long? userId = UserId();
        if (userId is null)
        {
            return Forbid();
        }

        await LoadAsync(userId.Value, cancellationToken).ConfigureAwait(false);

        if (edit is Guid uuid)
        {
            Editing = await _access
                            .FindAsync(uuid, userId.Value, CameraOperation.ManageCamera, cancellationToken)
                            .ConfigureAwait(false);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAddNetworkAsync(string? name,
                                                           string source,
                                                           int teamId,
                                                           int? printerId,
                                                           CancellationToken cancellationToken)
    {
        long? userId = UserId();
        if (userId is null)
        {
            return Forbid();
        }

        CameraSaveOutcome outcome = await _cameras
                                          .CreateAsync(userId.Value, teamId, name, source, printerId, cancellationToken)
                                          .ConfigureAwait(false);

        Report(outcome);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddAttachedAsync(string? name,
                                                            string device,
                                                            int teamId,
                                                            int? printerId,
                                                            CancellationToken cancellationToken)
    {
        long? userId = UserId();
        if (userId is null)
        {
            return Forbid();
        }

        // The source is built here rather than typed, which is what puts input_format=mjpeg on it -
        // the setting whose absence costs a transcode per frame and is invisible when wrong.
        string source = LocalCameraDevices.SourceFor(device);

        CameraSaveOutcome outcome = await _cameras
                                          .CreateAsync(userId.Value, teamId, name, source, printerId, cancellationToken)
                                          .ConfigureAwait(false);

        Report(outcome);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEditAsync(Guid uuid,
                                                     string? name,
                                                     string source,
                                                     int? printerId,
                                                     CancellationToken cancellationToken)
    {
        long? userId = UserId();
        if (userId is null)
        {
            return Forbid();
        }

        CameraSaveOutcome outcome = await _cameras
                                          .UpdateAsync(userId.Value, uuid, name, source, printerId, cancellationToken)
                                          .ConfigureAwait(false);

        Report(outcome);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid uuid, CancellationToken cancellationToken)
    {
        long? userId = UserId();
        if (userId is null)
        {
            return Forbid();
        }

        bool removed = await _cameras.DeleteAsync(userId.Value, uuid, cancellationToken).ConfigureAwait(false);

        if (removed)
        {
            Message = _localiser["Cameras_Removed"];
        }
        else
        {
            Error = _localiser["Cameras_NotYours"];
        }

        return RedirectToPage();
    }

    /// <summary>
    /// How old this camera's last picture is, or null if there is nothing current.
    /// </summary>
    public string? FrameAge(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);

        CameraFrame? frame = _frames.Current(camera.Id);
        if (frame is null)
        {
            return null;
        }

        TimeSpan age = frame.AgeAt(DateTimeOffset.UtcNow);

        return age.TotalSeconds < 2 ? "just now" : string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalSeconds}s ago");
    }

    private async Task LoadAsync(long userId, CancellationToken cancellationToken)
    {
        Cameras = await _access.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        Teams = await _access.ManageableTeamsAsync(userId, cancellationToken).ConfigureAwait(false);
        IsAdministrator = await _access.IsAdministratorAsync(userId, cancellationToken).ConfigureAwait(false);

        Printers = await _printers.ListPrintersForUserAsync(userId, cancellationToken).ConfigureAwait(false);

        AvailableDevices = IsAdministrator ? await _cameras.AvailableDevicesAsync(cancellationToken).ConfigureAwait(false) : [];
    }

    private void Report(CameraSaveOutcome outcome)
    {
        if (!outcome.Saved)
        {
            Error = outcome.Error;
            return;
        }

        if (outcome.Warning is not null)
        {
            Warning = outcome.Warning;
            return;
        }

        Message = _localiser["Cameras_SavedWithPicture"];
    }

    private long? UserId()
    {
        return long.TryParse(_userManager.GetUserId(User), CultureInfo.InvariantCulture, out long id) ? id : null;
    }
}
