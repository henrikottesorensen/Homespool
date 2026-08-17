using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

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
/// <c>/api/v1</c> without reproducing the sign-in and antiforgery dance in bash
/// (<c>notes/api-tokens.md</c>).
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
        /// mint one, so unticking everything is far likelier to be a slip than a wish.
        /// </remarks>
        [MinLength(1, ErrorMessage = "Tokens_ScopeRequired")]
        public IList<Capability> Scope { get; set; } = [];
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        // Everything ticked, so the form opens on the credential somebody expects and narrowing is a
        // deliberate act rather than a chore. It is also exactly what tokens were before scopes.
        Input.Scope = [.. CapabilitySet.Everything];

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
        Input = new InputModel { Scope = [.. CapabilitySet.Everything] };

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
