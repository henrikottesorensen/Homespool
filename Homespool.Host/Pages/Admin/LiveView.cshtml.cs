using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

using Homespool.Host.Cameras;
using Homespool.Host.Localisation;
using Homespool.Host.Services;

namespace Homespool.Host.Pages.Admin;

/// <summary>
/// Whether the camera stream server may ask a public STUN server for this deployment's own public
/// address, and what turning that on means.
/// </summary>
/// <remarks>
/// <para>
/// <b>A page rather than a setting in <c>.env</c>, and the reason is the question it has to ask.</b>
/// Every other deployment-wide setting here lives in configuration, which is the right home for
/// something set once while standing a stack up. This one has a consequence that has to be read
/// before it is chosen — a file cannot put two named conditions in front of somebody and wait.
/// </para>
/// <para>
/// <b>The prompt follows the <c>Set Ready</c> checklist</b>: named
/// conditions rather than a bare "are you sure", and a confirm button carrying the assertion rather
/// than agreement. The two conditions are that your public address is placed in every offer, which
/// is inherent to a reflexive candidate rather than a go2rtc quirk, and that a third party is
/// contacted to discover it, so the deployment stops being self-contained. Both are true, both are
/// weighable, and neither is visible from the outcome if it is simply switched on.
/// </para>
/// <para>
/// <b>Administrator only.</b> It is a property of the deployment's relationship with the outside
/// world rather than of any one team's cameras, which is the same reasoning that puts an attached
/// camera behind administrator rather than <c>CanManage</c>.
/// </para>
/// </remarks>
[Authorize(Roles = AdminBootstrap.AdminRole)]
public class LiveViewModel : PageModel
{
    private readonly DeploymentSettingStore _settings;
    private readonly WebRtcSidecarWriter _writer;
    private readonly CameraLiveAvailability _availability;
    private readonly IOptions<CameraOptions> _cameras;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public LiveViewModel(DeploymentSettingStore settings,
                         WebRtcSidecarWriter writer,
                         CameraLiveAvailability availability,
                         IOptions<CameraOptions> cameras,
                         IStringLocalizer<SharedResource> localiser)
    {
        _settings = settings;
        _writer = writer;
        _availability = availability;
        _cameras = cameras;
        _localiser = localiser;
    }

    /// <summary>Whether STUN is currently allowed.</summary>
    public bool StunEnabled { get; private set; }

    /// <summary>The STUN server that would be contacted, so the prompt can name it.</summary>
    public string StunServer => _cameras.Value.WebRtcStunServer;

    /// <summary>The address browsers are told to send media to, or empty if live view is off.</summary>
    public string Candidate => _availability.Candidate;

    /// <summary>Whether live view works at all here.</summary>
    public bool LiveViewAvailable => _availability.IsConfigured;

    /// <summary>Outcome of the last change, if there was one.</summary>
    public string? StatusMessage { get; private set; }

    /// <summary>Whether <see cref="StatusMessage"/> reports success.</summary>
    public bool StatusSuccess { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        StunEnabled = (await _settings.GetAsync(cancellationToken)).WebRtcStunEnabled;
    }

    public async Task<IActionResult> OnPostAsync(bool enabled, CancellationToken cancellationToken)
    {
        // Recorded before it is applied, deliberately. A sidecar that cannot be reached right now
        // must not lose the choice somebody made - the next start applies it instead, because the
        // startup path asks the database rather than the sidecar what was wanted.
        await _settings.SetStunEnabledAsync(enabled, cancellationToken);

        StunEnabled = enabled;

        bool applied = await _writer.EnsureAsync(_availability.Candidate, enabled, cancellationToken);

        StatusSuccess = applied;
        StatusMessage = applied
            ? _localiser[enabled ? "LiveView_StunOnApplied" : "LiveView_StunOffApplied"].Value
            : _localiser["LiveView_NotApplied"].Value;

        return Page();
    }
}
