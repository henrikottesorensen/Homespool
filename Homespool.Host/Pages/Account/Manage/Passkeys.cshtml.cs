using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Host.RateLimiting;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Account.Manage;

/// <summary>
/// The signed-in account's passkeys: add one, rename one, remove one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Adding one is the registration ceremony</b>, the mirror of the sign-in the <c>Passkey</c>
/// scheme runs: the page asks the engine for creation options and starts a ceremony in
/// <see cref="PasskeyCeremonies"/>, the script hands them to <c>navigator.credentials.create</c>, and
/// the answer comes back to be verified against that ceremony and stored. The same engine, the same
/// cookie, the same single-use rule; only the operation tag differs, and a sign-in ceremony answered
/// here is refused for it.
/// </para>
/// <para>
/// <b>Adding one takes a recent proof.</b> A session is not enough: a cookie somebody else got hold
/// of, or a browser left unlocked, would otherwise mint a durable, phishing-resistant sign-in that a
/// later password change does not touch. The handler that starts the ceremony carries
/// <see cref="RequireRecentProofAttribute"/>; the proof is earned at <c>Account/Reauthenticate</c>
/// with any credential the account holds - the password, another passkey, or the account's provider -
/// and this page holds no check of its own. The view offers the way there instead of the add form
/// while the proof is missing, because the script asks for the ceremony with <c>fetch</c> and would
/// otherwise follow the filter's redirect into a page it cannot read. The five-minute ceremony is what
/// the proof unlocks; the answer needs no second proof.
/// </para>
/// <para>
/// <b>Removing the last one is allowed.</b> A passkey is a complete sign-in beside whatever else the
/// account holds, a password or a provider, rather than a factor either needs, so no removal can
/// strand anybody. The administrator's revoke on <c>Admin/Passkeys</c> is the recovery for a lost
/// device. <b>A password change or reset leaves passkeys standing</b>, unlike API tokens, which a
/// reset deletes: they are the person's own daily sign-in, not a machine's, and nothing can add one
/// without the password,
/// so what a change cannot rule out is only a passkey added by somebody who proved themselves on
/// this browser - which both password pages tell the person to go and look for.
/// </para>
/// <para>
/// <b>Offered only where the relying-party id covers the host</b>, as the login page's button is:
/// a credential minted here is bound to that name, and a ceremony from any other fails in the browser
/// with nothing to say why.
/// </para>
/// </remarks>
[Authorize]
[EnableRateLimiting(RateLimitPolicies.PasskeyChallenge)]
public class PasskeysModel : PageModel
{
    /// <summary>The handler that starts a registration ceremony; the one handler on this page the rate limit applies to.</summary>
    public const string BeginRegistrationHandler = "BeginRegistration";

    /// <summary>The longest name a passkey may be given. It is rendered in a table row and nowhere else.</summary>
    public const int NameMaxLength = 64;

    private readonly UserManager<HSUser> _users;
    private readonly RecentProof _proof;
    private readonly IPasskeyHandler<HSUser> _engine;
    private readonly PasskeyCeremonies _ceremonies;
    private readonly IOptionsMonitor<PasskeyAuthenticationOptions> _options;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<PasskeysModel> _logger;

    public PasskeysModel(UserManager<HSUser> users,
                         RecentProof proof,
                         IPasskeyHandler<HSUser> engine,
                         PasskeyCeremonies ceremonies,
                         IOptionsMonitor<PasskeyAuthenticationOptions> options,
                         IStringLocalizer<SharedResource> localiser,
                         ILogger<PasskeysModel> logger)
    {
        _users = users;
        _proof = proof;
        _engine = engine;
        _ceremonies = ceremonies;
        _options = options;
        _localiser = localiser;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<UserPasskeyInfo> Passkeys { get; private set; } = [];

    /// <summary>Whether a passkey can be added from the host this request arrived on.</summary>
    public bool PasskeysAvailable { get; private set; }

    /// <summary>Whether the person has proved themselves recently, so the add form is worth showing.</summary>
    public bool Proved { get; private set; }

    /// <summary>The relying-party id, for saying which address to come back by; null when none is configured.</summary>
    public string? ServerDomain { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public class InputModel
    {
        [StringLength(NameMaxLength, ErrorMessage = "Passkeys_NameInvalid")]
        [Display(Name = "Passkeys_NameLabel")]
        public string? Name { get; set; }
    }

    /// <summary>
    /// Whether <paramref name="name"/> may be a passkey's name: something to show, within the length,
    /// with no control characters and none of the invisible marks that would let a name render
    /// deceptively in the table - the rule the file store's directory name already applies.
    /// </summary>
    public static bool IsAcceptableName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > NameMaxLength)
        {
            return false;
        }

        foreach (char character in name)
        {
            // Written as escapes on purpose: these are invisible characters, and a source file holding
            // them literally is unreadable in a diff and carries the very hazard this rejects.
            if (char.IsControl(character) ||
                character is '\u200B' or '\u200C' or '\u200D' or '\uFEFF' ||
                character is >= '\u202A' and <= '\u202E' ||
                character is >= '\u2066' and <= '\u2069')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The credential id as the page and the forms spell it: base64url.</summary>
    public static string IdOf(UserPasskeyInfo passkey)
    {
        ArgumentNullException.ThrowIfNull(passkey);

        return Base64Url.EncodeToString(passkey.CredentialId);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        return await LoadAsync() ? Page() : NotFound();
    }

    /// <summary>
    /// The first half of adding a passkey: creation options for the browser, and a ceremony started.
    /// A POST so the antiforgery token guards it; 404 where passkeys are withheld. The recent proof is
    /// the filter's to demand, before this runs.
    /// </summary>
    [RequireRecentProof]
    public async Task<IActionResult> OnPostBeginRegistrationAsync(CancellationToken cancellationToken)
    {
        HSUser? user = await _users.GetUserAsync(User);

        if (user is null || !Scheme.Covers(Request.Host))
        {
            return NotFound();
        }

        PasskeyCreationOptionsResult creation = await _engine.MakeCreationOptionsAsync(
            new PasskeyUserEntity
            {
                Id = user.Id.ToString(CultureInfo.InvariantCulture),
                Name = user.UserName!,
                DisplayName = user.UserName!,
            },
            HttpContext);

        _ceremonies.Begin(HttpContext, PasskeyCeremonies.Attestation, creation.AttestationState!);

        Response.Headers.CacheControl = "no-store";

        return Content(creation.CreationOptionsJson, "application/json; charset=utf-8");
    }

    /// <summary>
    /// The second half: the browser's attestation, verified against the ceremony started above and
    /// stored under the name given. The proof was demanded when the ceremony began, and the ceremony
    /// cookie is the evidence it was.
    /// </summary>
    public async Task<IActionResult> OnPostRegisterAsync(string? credential)
    {
        HSUser? user = await _users.GetUserAsync(User);

        if (user is null)
        {
            return NotFound();
        }

        await LoadAsync();

        // The length attribute is the browser's half; this is the character rule, which no attribute
        // states. An empty name is fine here and gets the dated default below.
        if (!string.IsNullOrWhiteSpace(Input.Name) && !IsAcceptableName(Input.Name))
        {
            ModelState.AddModelError("Input.Name", _localiser["Passkeys_NameInvalid"]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        PasskeyCeremonies.Outcome ceremony = _ceremonies.Take(HttpContext, PasskeyCeremonies.Attestation);

        if (!ceremony.Succeeded || string.IsNullOrWhiteSpace(credential))
        {
            _logger.LogInformation("Passkey registration refused for user {UserId}: {Reason}.",
                                   user.Id,
                                   ceremony.Succeeded ? "no credential was posted" : ceremony.Reason);

            return Refused();
        }

        PasskeyAttestationResult attested = await _engine.PerformAttestationAsync(new PasskeyAttestationContext
        {
            HttpContext = HttpContext,
            CredentialJson = credential,
            AttestationState = ceremony.EngineState,
        });

        if (!attested.Succeeded)
        {
            // The engine quotes fields of the posted credential in its reason, so it is cleaned and cut.
            _logger.LogInformation("Passkey registration refused for user {UserId}: {Reason}",
                                   user.Id,
                                   LogText.Clean(attested.Failure?.Message, PasskeyAuthenticationHandler.MaxLoggedFailureLength));

            return Refused();
        }

        // The ceremony was started for this account and the cookie is bound to this browser, so the
        // entity the engine hands back can only be this account's. Checked anyway: a mismatch here
        // would mean the ceremony state was not ours, and storing a credential under the wrong
        // account is the one outcome worse than refusing.
        if (!string.Equals(attested.UserEntity!.Id, user.Id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            _logger.LogWarning("Passkey registration refused: the ceremony named user {CeremonyUserId}, the session is user {UserId}.",
                               attested.UserEntity.Id,
                               user.Id);

            return Refused();
        }

        // Verified and for this account, so now it is answered; a record refused is a concurrent
        // copy of this request that got there first.
        if (_ceremonies.Spend(ceremony) is { } notSpent)
        {
            _logger.LogInformation("Passkey registration refused for user {UserId}: {Reason}.", user.Id, notSpent);

            return Refused();
        }

        UserPasskeyInfo passkey = attested.Passkey!;
        passkey.Name = string.IsNullOrWhiteSpace(Input.Name) ?
            _localiser["Passkeys_DefaultName", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)].Value :
            Input.Name.Trim();

        IdentityResult stored = await _users.AddOrUpdatePasskeyAsync(user, passkey);

        if (!stored.Succeeded)
        {
            _logger.LogError("Passkey registration for user {UserId} could not be stored.", user.Id);

            return Refused();
        }

        _logger.LogInformation("User {UserId} added passkey {PasskeyName}{Synced}.",
                               user.Id,
                               passkey.Name,
                               passkey.IsBackupEligible ? " (synced)" : string.Empty);

        StatusMessage = _localiser["Passkeys_Added"];

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRenameAsync(string? id, string? name)
    {
        HSUser? user = await _users.GetUserAsync(User);

        if (user is null || !TryDecode(id, out byte[] credentialId))
        {
            return NotFound();
        }

        string trimmed = name?.Trim() ?? string.Empty;

        if (!IsAcceptableName(trimmed))
        {
            await LoadAsync();
            ModelState.AddModelError(string.Empty, _localiser["Passkeys_NameInvalid"]);

            return Page();
        }

        UserPasskeyInfo? passkey = await _users.GetPasskeyAsync(user, credentialId);

        if (passkey is null)
        {
            StatusMessage = _localiser["Passkeys_Gone"];

            return RedirectToPage();
        }

        passkey.Name = trimmed;
        await _users.AddOrUpdatePasskeyAsync(user, passkey);

        StatusMessage = _localiser["Passkeys_Renamed"];

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(string? id)
    {
        HSUser? user = await _users.GetUserAsync(User);

        if (user is null || !TryDecode(id, out byte[] credentialId))
        {
            return NotFound();
        }

        // Looked up first because the store's remove says nothing about whether there was anything
        // to remove - and "already gone" covers somebody else's id too, rather than reporting on the
        // existence of other people's credentials.
        UserPasskeyInfo? passkey = await _users.GetPasskeyAsync(user, credentialId);

        if (passkey is null)
        {
            StatusMessage = _localiser["Passkeys_Gone"];

            return RedirectToPage();
        }

        await _users.RemovePasskeyAsync(user, credentialId);

        _logger.LogInformation("User {UserId} removed passkey {PasskeyName}.", user.Id, passkey.Name);

        StatusMessage = _localiser["Passkeys_Removed"];

        return RedirectToPage();
    }

    private PasskeyAuthenticationOptions Scheme => _options.Get(Schemes.Passkey);

    private IActionResult Refused()
    {
        ModelState.AddModelError(string.Empty, _localiser["Passkeys_RegistrationFailed"]);

        return Page();
    }

    private static bool TryDecode(string? id, out byte[] credentialId)
    {
        credentialId = [];

        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        try
        {
            credentialId = Base64Url.DecodeFromChars(id);

            return credentialId.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<bool> LoadAsync()
    {
        HSUser? user = await _users.GetUserAsync(User);

        if (user is null)
        {
            return false;
        }

        Passkeys = [.. await _users.GetPasskeysAsync(user)];
        PasskeysAvailable = Scheme.Covers(Request.Host);
        Proved = _proof.IsProved(HttpContext, user.Id);
        ServerDomain = Scheme.IsConfigured ? Scheme.ServerDomain!.Trim() : null;

        return true;
    }
}
