using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Account.Manage;

/// <summary>
/// Create, list and revoke personal access tokens — the credential that lets a script call
/// <c>/api/v1</c> without reproducing the sign-in and antiforgery dance in bash.
/// </summary>
/// <remarks>
/// <para>
/// <b>The new token is rendered by the POST itself rather than carried through a redirect</b>, which
/// is a deliberate break from the post/redirect/get the sibling pages use. Their
/// <c>StatusMessage</c> travels in <c>TempData</c>, which is a Data-Protection-encrypted cookie — a
/// fine place for "your password has been changed" and the wrong place for a bearer credential. This
/// way the secret exists in exactly one HTTP response, the one that minted it. The cost is that
/// refreshing that response re-submits the form and mints a second token; it is visible in the list
/// below and revocable in one click, which is the cheaper of the two prices.
/// </para>
/// <para>
/// Revocation does redirect, because there is nothing secret to carry.
/// </para>
/// <para>
/// <b>Creating one takes the current password.</b> A session is not enough: a token is a complete
/// sign-in for everything its scope names, it has no expiry, and a password change leaves it
/// standing - so a cookie somebody else got hold of, or a browser left unlocked, would otherwise mint
/// a credential that outlives the session that minted it. The password is demanded through
/// <see cref="StepUpGate"/>, as the passkey page demands it before a registration: a wrong guess
/// backs off the account's step-ups rather than its sign-in, and an account created through a
/// provider re-authenticates there instead, with the proof scoped to this page. Revoking takes
/// nothing extra, because removing a credential is what the holder of a stolen session would least
/// want to do.
/// </para>
/// </remarks>
[Authorize]
public class ApiTokensModel : PageModel
{
    private readonly ApiTokenService _tokens;
    private readonly UserManager<HSUser> _userManager;
    private readonly StepUpGate _stepUp;
    private readonly StepUpText _stepUpText;
    private readonly ExternalSignIn _externalSignIn;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<ApiTokensModel> _logger;

    public ApiTokensModel(ApiTokenService tokens,
                          UserManager<HSUser> userManager,
                          StepUpGate stepUp,
                          StepUpText stepUpText,
                          ExternalSignIn externalSignIn,
                          ILogger<ApiTokensModel> logger,
                          IStringLocalizer<SharedResource> localiser,
                          CapabilityText capabilities)
    {
        _tokens = tokens;
        _userManager = userManager;
        _stepUp = stepUp;
        _stepUpText = stepUpText;
        _externalSignIn = externalSignIn;
        _localiser = localiser;
        _logger = logger;
        Capabilities = capabilities;
    }

    /// <summary>
    /// Whether this account proves itself with a password. False puts the provider round trip on the
    /// page instead, because there is no password to ask for.
    /// </summary>
    public bool UsesPassword { get; private set; }

    /// <summary>The providers a password-less account can be sent to, for the button that sends it.</summary>
    public IReadOnlyList<AuthenticationScheme> Providers { get; private set; } = [];

    /// <summary>Names the capabilities for both the form and the listing, so the two cannot disagree.</summary>
    public CapabilityText Capabilities { get; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>This user's tokens, newest first. Only ever their own.</summary>
    public IReadOnlyList<ApiToken> Tokens { get; private set; } = [];

    /// <summary>
    /// The plaintext of a token just created, for the single render that shows it. Null on every
    /// other request, and unrecoverable afterwards: only its hash was stored.
    /// </summary>
    public string? CreatedToken { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public class InputModel
    {
        [Required]
        [StringLength(ApiToken.NameMaxLength, MinimumLength = 1)]
        [Display(Name = "Manage_TokenName")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// What the token may do. Ticked boxes, so the default is what the form renders rather than
        /// what this field says.
        /// </summary>
        /// <remarks>
        /// <b>At least one is required, though an empty scope is representable on purpose.</b> A token
        /// that can do nothing is a thing the model must be able to express - it is what keeps "empty"
        /// from being overloaded to mean "unrestricted" - but nobody arrives at this form intending to
        /// mint one.
        /// <para>
        /// <b>The refusal carries more weight now the form opens empty</b>, because submitting an
        /// empty scope stopped needing a deliberate untick and became the thing that happens if the
        /// picker is not noticed at all. It is the one check standing between "I typed a name and
        /// pressed the button" and a credential that silently does nothing, which is a failure the
        /// script only discovers later and reads as a bug rather than as a scope.
        /// </para>
        /// </remarks>
        [MinLength(1, ErrorMessage = "Tokens_ScopeRequired")]
        public IList<Capability> Scope { get; set; } = [];

        /// <summary>The proof that the person at the keyboard holds the account, not only a session.</summary>
        /// <remarks>Not required by attribute: an account created through a provider has none, and proves itself at the provider instead.</remarks>
        [DataType(DataType.Password)]
        [Display(Name = "Account_Password")]
        public string? Password { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        // Nothing ticked. A credential's default has to be the powerless one: what somebody does not
        // think about should be what they cannot do, and every box already ticked is a decision made
        // on their behalf and in the wrong direction. Tick all is one click away for the rare token
        // that genuinely wants everything, so the ergonomic cost falls on that case rather than on
        // every deliberately-scoped one.
        Input.Scope = [];

        return await LoadAsync(cancellationToken) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(user, cancellationToken);

            return Page();
        }

        // Proved before anything is minted, and refused in the form rather than through a redirect,
        // so the name and scope just typed survive the retry. The password never does: the field is
        // not re-rendered with a value.
        StepUpResult proof = await _stepUp.ProveAsync(HttpContext, user, Input.Password);

        if (!proof.Succeeded)
        {
            _logger.LogInformation("API token creation refused for user {UserId}: {Refusal}.", user.Id, proof.Refusal);

            ModelState.AddModelError(string.Empty, _stepUpText.Describe(proof));
            await LoadAsync(user, cancellationToken);

            return Page();
        }

        (ApiToken token, string plaintext) =
            await _tokens.CreateAsync(user.Id, Input.Name, Input.Scope, cancellationToken);

        // The scope is logged with it: "my script stopped working" is answered by knowing what the
        // key was minted able to do, and the scope is the one part of a token that is not secret.
        _logger.LogInformation("User {UserId} created API token {TokenId} scoped to {Scope}.",
                               user.Id, token.Id, token.Scope);

        CreatedToken = plaintext;

        // A fresh empty form rather than the scope just minted. Carrying it over would make the next
        // token default to the last one's rights, which is the same failure as defaulting to
        // everything - only quieter, because it looks like it was chosen.
        Input = new InputModel();

        // The secret is in this response body and must not outlive it. POST responses are already
        // non-cacheable under RFC 9111 absent explicit freshness information, which nothing here
        // sends, so this is hardening rather than a fix - but it does close one case that is real
        // rather than theoretical: the back/forward cache holds the rendered page in memory, so
        // without it a Back navigation can put the secret back on screen long after the person who
        // created it has walked away from a shared machine.
        Response.Headers.CacheControl = "no-store";

        // Listed after the create, so the new token appears in the table alongside its one-time secret.
        await LoadAsync(user, cancellationToken);

        return Page();
    }

    /// <summary>
    /// Sends a password-less account to its provider to re-authenticate, coming back to
    /// <see cref="OnGetReauthenticatedAsync"/> on this page - so the proof it earns is scoped to this
    /// page and cannot be spent on another.
    /// </summary>
    public async Task<IActionResult> OnPostReauthenticateAsync(string provider)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return NotFound();
        }

        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        string? redirectUrl = Url.Page("/Account/Manage/ApiTokens", pageHandler: "Reauthenticated");
        AuthenticationProperties? challenge = await _stepUp.ProviderChallengeAsync(user, provider, redirectUrl!);

        return challenge is null ? NotFound() : new ChallengeResult(provider, challenge);
    }

    /// <summary>The provider's answer, which becomes the proof the create below spends.</summary>
    public async Task<IActionResult> OnGetReauthenticatedAsync()
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return NotFound();
        }

        StatusMessage = _stepUpText.Describe(await _stepUp.RecordProviderProofAsync(HttpContext, user));

        return RedirectToPage();
    }

    /// <summary>
    /// Clears every box, so narrowing a token to the two capabilities a script needs is one click and
    /// two ticks rather than nine unticks.
    /// </summary>
    public Task<IActionResult> OnPostUntickAllAsync(CancellationToken cancellationToken)
    {
        return ReopenWithScopeAsync([], cancellationToken);
    }

    /// <summary>
    /// Ticks every box again, for somebody who cleared them and changed their mind.
    /// </summary>
    /// <remarks>
    /// <b>Worth knowing that this makes a maximal token one click away</b>, which cuts slightly
    /// against the narrowing the picker exists to encourage. It is here anyway: the form already opens
    /// in this state, so the button reaches nothing a reload would not, and a lone <i>untick</i> with
    /// no way back is a trap of its own.
    /// </remarks>
    public Task<IActionResult> OnPostTickAllAsync(CancellationToken cancellationToken)
    {
        return ReopenWithScopeAsync([.. CapabilitySet.Everything], cancellationToken);
    }

    /// <summary>
    /// Renders the form again with <paramref name="scope"/> ticked, keeping whatever has been typed
    /// into it and complaining about nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two buttons post rather than being script-only controls.</b> A button that does nothing
    /// where scripting is off looks broken, which is the failure <c>toggle-submit.js</c> is already
    /// designed against; here the round trip <i>is</i> the fallback. <c>token-scope.js</c> sets the
    /// boxes in the browser and cancels the submit, so this is what a browser without scripting
    /// reaches rather than what anybody normally gets.
    /// </para>
    /// <para>
    /// <b>Validation is dropped rather than run.</b> Binding has already recorded that
    /// <see cref="InputModel.Name"/> is required and that the scope was too short, against a form
    /// nobody has finished filling in. Neither button is an attempt to mint anything, so reporting
    /// either would be scolding somebody for a step they have not reached yet.
    /// </para>
    /// </remarks>
    private async Task<IActionResult> ReopenWithScopeAsync(IList<Capability> scope,
                                                           CancellationToken cancellationToken)
    {
        // Cleared rather than merely ignored: the tag helpers render from ModelState in preference to
        // the model, so leaving the errors in place would print them beside the fields. Input keeps
        // what was typed, which is what the fields fall back to.
        ModelState.Clear();

        Input.Scope = scope;

        return await LoadAsync(cancellationToken) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostRevokeAsync(long id, CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return NotFound();
        }

        // False means it was not this user's to revoke - which covers "already gone" too, and says the
        // same thing either way rather than reporting on the existence of other people's tokens.
        bool revoked = await _tokens.RevokeAsync(user.Id, id, cancellationToken);

        if (revoked)
        {
            _logger.LogInformation("User {UserId} revoked API token {TokenId}.", user.Id, id);
        }

        StatusMessage = revoked ? _localiser["Manage_TokenRevoked"] : _localiser["Manage_TokenGone"];

        return RedirectToPage();
    }

    private async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return false;
        }

        await LoadAsync(user, cancellationToken);

        return true;
    }

    private async Task LoadAsync(HSUser user, CancellationToken cancellationToken)
    {
        Tokens = await _tokens.ListAsync(user.Id, cancellationToken);
        UsesPassword = await _stepUp.UsesPasswordAsync(user);
        Providers = UsesPassword ? [] : await _externalSignIn.ProvidersAsync();
    }
}
