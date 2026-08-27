using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Homespool.Host.Localisation;
using Homespool.Host.Services;
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
/// </remarks>
[Authorize]
public class ApiTokensModel : PageModel
{
    private readonly ApiTokenService _tokens;
    private readonly UserManager<HSUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<ApiTokensModel> _logger;

    public ApiTokensModel(ApiTokenService tokens, UserManager<HSUser> userManager, ILogger<ApiTokensModel> logger,
                          IStringLocalizer<SharedResource> localiser, CapabilityText capabilities)
    {
        _tokens = tokens;
        _userManager = userManager;
        _localiser = localiser;
        _logger = logger;
        Capabilities = capabilities;
    }

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
            Tokens = await _tokens.ListAsync(user.Id, cancellationToken);

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
        Tokens = await _tokens.ListAsync(user.Id, cancellationToken);

        return Page();
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

        Tokens = await _tokens.ListAsync(user.Id, cancellationToken);

        return true;
    }
}
