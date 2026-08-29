using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

using Homespool.Host.Accounts;
using Homespool.Host.Cameras;
using Homespool.Host.Localisation;

namespace Homespool.Host.Pages.Admin;

/// <summary>
/// What live camera viewing is doing on this deployment: whether it works at all, the address
/// browsers are told to send media to, and whether STUN is allowed.
/// </summary>
/// <remarks>
/// <b>It reports; it no longer decides.</b> The STUN switch moved to the settings page when the row
/// behind it became an ordinary option, and it took its prompt with it. What is left has no home
/// there: an address this deployment discovered and a yes-or-no about whether the feature can work
/// are status rather than configuration, and the health banner sends an administrator here to read
/// them.
/// </remarks>
[Authorize(Roles = AdminBootstrap.AdminRole)]
public class LiveViewModel : PageModel
{
    private readonly CameraLiveAvailability _availability;
    private readonly IOptionsSnapshot<CameraOptions> _cameras;
    private readonly IStringLocalizer<SharedResource> _localiser;

    public LiveViewModel(CameraLiveAvailability availability,
                         IOptionsSnapshot<CameraOptions> cameras,
                         IStringLocalizer<SharedResource> localiser)
    {
        _availability = availability;
        _cameras = cameras;
        _localiser = localiser;
    }

    /// <summary>Whether STUN is currently allowed.</summary>
    public bool StunEnabled => _cameras.Value.WebRtcStunEnabled;

    /// <summary>The STUN server that would be contacted, so the prompt can name it.</summary>
    public string StunServer => _cameras.Value.WebRtcStunServer;

    /// <summary>The address browsers are told to send media to, or empty if live view is off.</summary>
    public string Candidate => _availability.Candidate;

    /// <summary>Whether live view works at all here.</summary>
    public bool LiveViewAvailable => _availability.IsConfigured;

    /// <summary>Shows what live viewing is currently doing.</summary>
    public void OnGet()
    {
    }
}
