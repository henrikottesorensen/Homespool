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
using Homespool.Model;
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
    private readonly ErrorText _errors;

    public IndexModel(CameraService cameras,
                      CameraAccessService access,
                      Go2RtcClient streamServer,
                      CameraFrameCache frames,
                      PrinterQueryService printers,
                      CameraDisplayNames names,
                      UserManager<HSUser> userManager,
                      IStringLocalizer<SharedResource> localiser,
                      ErrorText errors)
    {
        _cameras = cameras;
        _access = access;
        _streamServer = streamServer;
        _frames = frames;
        _printers = printers;
        _names = names;
        _userManager = userManager;
        _localiser = localiser;
        _errors = errors;
    }

    /// <summary>The cameras this account may see.</summary>
    public IReadOnlyList<Camera> Cameras { get; private set; } = [];

    /// <summary>Devices plugged into this machine that nobody has claimed.</summary>
    /// <remarks>Always empty for a non-administrator, since they could not claim one anyway.</remarks>
    public IReadOnlyList<LocalCameraDevice> AvailableDevices { get; private set; } = [];

    /// <summary>
    /// The MJPEG capture sizes each attached device offers, keyed by its by-id name. Empty for a
    /// device the stream server has not enumerated - which is what the rescan button is for.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DeviceResolutions { get; private set; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>The sizes the camera being edited offers, empty when it is not an attached one.</summary>
    public IReadOnlyList<string> EditingResolutions { get; private set; } = [];

    /// <summary>Teams this account may add a camera to.</summary>
    public IReadOnlyList<Team> Teams { get; private set; } = [];

    /// <summary>Printers a camera can be pointed at.</summary>
    public IReadOnlyList<Printer> Printers { get; private set; } = [];

    /// <summary>Whether this account may claim a camera attached to the server.</summary>
    public bool IsAdministrator { get; private set; }

    /// <summary>The camera being edited, if the query string names one.</summary>
    public Camera? Editing { get; private set; }

    /// <summary>
    /// What the edit form puts in its source box: the address, and the password as a placeholder.
    /// </summary>
    /// <remarks>
    /// <b>Composed from the stored columns rather than decrypted.</b> The user name is stored in the
    /// clear and the placeholder stands in for the password, so rendering this form never needs the
    /// key ring and the real password never reaches a browser. A camera saved before the split still
    /// carries its credential inline in the address, so that case is masked the old way.
    /// </remarks>
    public string EditingSource
    {
        get
        {
            if (Editing is null)
            {
                return string.Empty;
            }

            if (Editing.CredentialUser is null)
            {
                return CameraSourceDisplay.WithHiddenPassword(Editing.Source);
            }

            return CameraSourceDisplay.WithCredential(
                Editing.Source,
                Editing.CredentialUser,
                Editing.CredentialSecret is null ? null : CameraSourceDisplay.HiddenPassword);
        }
    }

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
                            .FindAsync(uuid, CallerResolver.For(userId.Value, User), Capability.ManageCamera, cancellationToken)
                            .ConfigureAwait(false);

            // The camera being edited holds its device, so it is not in AvailableDevices - that list
            // is deliberately the unclaimed ones. Its sizes are asked for separately.
            if (Editing is not null && LocalCameraDevices.DeviceNameFrom(Editing.Source) is { } device
                && CameraSourcePolicy.IsLocalDevice(Editing.Source))
            {
                IReadOnlyDictionary<string, IReadOnlyList<string>> byDevice = await _cameras
                    .DeviceResolutionsAsync([new LocalCameraDevice(device, device)], cancellationToken)
                    .ConfigureAwait(false);

                EditingResolutions = byDevice.TryGetValue(device, out IReadOnlyList<string>? sizes) ? sizes : [];
            }
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
                                          .CreateAsync(CallerResolver.For(userId.Value, User), teamId, name, source, printerId, resolution: null, cancellationToken)
                                          .ConfigureAwait(false);

        Report(outcome);

        return RedirectToPage();
    }

    /// <summary>
    /// Restarts the stream server so it reads the attached devices again.
    /// </summary>
    /// <remarks>
    /// <b>Administrator only, like claiming a device.</b> The cost falls on everybody watching a
    /// camera, and the thing it fixes - a device list that predates a camera being plugged in - is a
    /// property of the server rather than of a team. See <see cref="CameraService.RescanDevicesAsync"/>
    /// for why a restart is the only mechanism.
    /// </remarks>
    public async Task<IActionResult> OnPostRescanAsync(CancellationToken cancellationToken)
    {
        long? userId = UserId();
        if (userId is null)
        {
            return Forbid();
        }

        if (!await _access.IsAdministratorAsync(userId.Value, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }

        bool restarted = await _cameras.RescanDevicesAsync(cancellationToken).ConfigureAwait(false);

        // A sidecar that drops the connection while restarting is doing as it was asked, so an
        // unacknowledged restart is reported as the ordinary outcome - the reloaded device list is
        // the honest answer either way.
        if (restarted)
        {
            Message = _localiser["Cameras_Rescanned"];
        }
        else
        {
            Warning = _localiser["Cameras_RescanUnconfirmed"];
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddAttachedAsync(string? name,
                                                            string device,
                                                            int teamId,
                                                            int? printerId,
                                                            string? resolution,
                                                            CancellationToken cancellationToken)
    {
        long? userId = UserId();
        if (userId is null)
        {
            return Forbid();
        }

        // The source is built here rather than typed, which is what puts input_format=mjpeg on it -
        // the setting whose absence costs a transcode per frame and is invisible when wrong.
        string source = LocalCameraDevices.SourceFor(device, resolution);

        CameraSaveOutcome outcome = await _cameras
                                          .CreateAsync(CallerResolver.For(userId.Value, User), teamId, name, source, printerId, resolution, cancellationToken)
                                          .ConfigureAwait(false);

        Report(outcome);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEditAsync(Guid uuid,
                                                     string? name,
                                                     string source,
                                                     int? printerId,
                                                     string? resolution,
                                                     CancellationToken cancellationToken)
    {
        long? userId = UserId();
        if (userId is null)
        {
            return Forbid();
        }

        CameraSaveOutcome outcome = await _cameras
                                          .UpdateAsync(CallerResolver.For(userId.Value, User), uuid, name, source, printerId, resolution, cancellationToken)
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

        bool removed = await _cameras.DeleteAsync(CallerResolver.For(userId.Value, User), uuid, cancellationToken).ConfigureAwait(false);

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

        return age.TotalSeconds < 2 ?
            _localiser["Cameras_JustNow"] :
            _localiser["Cameras_SecondsAgo", (int)age.TotalSeconds];
    }

    private async Task LoadAsync(long userId, CancellationToken cancellationToken)
    {
        Cameras = await _access.ListAsync(CallerResolver.For(userId, User), cancellationToken).ConfigureAwait(false);
        Teams = await _access.ManageableTeamsAsync(CallerResolver.For(userId, User), cancellationToken).ConfigureAwait(false);
        IsAdministrator = await _access.IsAdministratorAsync(userId, cancellationToken).ConfigureAwait(false);

        Printers = await _printers.ListPrintersForUserAsync(CallerResolver.For(userId, User), cancellationToken).ConfigureAwait(false);

        AvailableDevices = IsAdministrator ? await _cameras.AvailableDevicesAsync(cancellationToken).ConfigureAwait(false) : [];

        DeviceResolutions = IsAdministrator && AvailableDevices.Count > 0
            ? await _cameras.DeviceResolutionsAsync(AvailableDevices, cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    }

    private void Report(CameraSaveOutcome outcome)
    {
        // The service chose the sentence; this chooses the language. Splitting it that way is what
        // lets CameraService and CameraSourcePolicy stay free of the resource system - see MessageKey.
        if (!outcome.Saved)
        {
            Error = outcome.Error is null ? null : _errors.For(outcome.Error);
            return;
        }

        if (outcome.Warning is not null)
        {
            Warning = _errors.For(outcome.Warning);
            return;
        }

        Message = _localiser["Cameras_SavedWithPicture"];
    }

    private long? UserId()
    {
        return long.TryParse(_userManager.GetUserId(User), CultureInfo.InvariantCulture, out long id) ? id : null;
    }
}
