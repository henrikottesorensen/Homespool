using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Host.Localisation;
using Homespool.Host.Pages.Printers;
using Homespool.Model;
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
/// <b>Adding one takes the current password.</b> A session is not enough: a cookie somebody else got
/// hold of, or a browser left unlocked, would otherwise mint a durable, phishing-resistant sign-in
/// that a later password change does not touch. So the challenge is issued only after the password
/// is proved, under a backoff of its own (<see cref="LimitedAction.AddPasskey"/>) because a password
/// check on an authenticated path is otherwise unlimited guesses. The five-minute ceremony is what the
/// password unlocks; the answer needs no second proof. <b>An account created through an external
/// provider has no password to prove, and re-authenticates at the provider instead</b>: a challenge
/// to the provider's scheme with <c>max_age=0</c> and <c>prompt=login</c>, a callback that checks the
/// subject is the one this account already signs in with and that any <c>auth_time</c> the provider
/// reports is recent, and a five-minute proof kept the way a ceremony is, spent by the registration.
/// A provider that ignores <c>max_age</c> and reports no <c>auth_time</c> - dex's mock connector is
/// one - still has to complete the round trip in the caller's own browser, which a stolen cookie
/// cannot; what it does not defend is an unlocked browser at a provider that never asks again.
/// </para>
/// <para>
/// <b>Removing the last one is allowed.</b> A passkey is a complete sign-in beside whatever else the
/// account holds, a password or a provider, rather than a factor either needs, so no removal can
/// strand anybody. The administrator's revoke on <c>Admin/Passkeys</c> is the recovery for a lost
/// device. <b>A password change or reset leaves passkeys standing</b>, unlike API tokens: they are
/// the person's own daily sign-in, not a machine's, and nothing can add one without the password,
/// so what a change cannot rule out is only a passkey added by somebody who knew it - which both
/// password pages tell the person to go and look for.
/// </para>
/// <para>
/// <b>Offered only where the relying-party id covers the host</b>, as the login page's button is:
/// a credential minted here is bound to that name, and a ceremony from any other fails in the browser
/// with nothing to say why.
/// </para>
/// </remarks>
[Authorize]
public class PasskeysModel : PageModel
{
    /// <summary>The longest name a passkey may be given. It is rendered in a table row and nowhere else.</summary>
    public const int NameMaxLength = 64;

    /// <summary>
    /// How old a provider's <c>auth_time</c> may be for the round trip to count as a re-authentication.
    /// Generous next to the ceremony's five minutes, because the person spent some of it at the
    /// provider's own screens.
    /// </summary>
    public static readonly TimeSpan MaxProviderProofAge = TimeSpan.FromMinutes(2);

    private readonly UserManager<HSUser> _users;
    private readonly SignInManager<HSUser> _signInManager;
    private readonly IPasskeyHandler<HSUser> _engine;
    private readonly PasskeyCeremonies _ceremonies;
    private readonly IOptionsMonitor<PasskeyAuthenticationOptions> _options;
    private readonly AttemptLimiter _attemptLimiter;
    private readonly TimeProvider _timeProvider;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<PasskeysModel> _logger;

    public PasskeysModel(UserManager<HSUser> users,
                         SignInManager<HSUser> signInManager,
                         IPasskeyHandler<HSUser> engine,
                         PasskeyCeremonies ceremonies,
                         IOptionsMonitor<PasskeyAuthenticationOptions> options,
                         AttemptLimiter attemptLimiter,
                         TimeProvider timeProvider,
                         IStringLocalizer<SharedResource> localiser,
                         ILogger<PasskeysModel> logger)
    {
        _users = users;
        _signInManager = signInManager;
        _engine = engine;
        _ceremonies = ceremonies;
        _options = options;
        _attemptLimiter = attemptLimiter;
        _timeProvider = timeProvider;
        _localiser = localiser;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<UserPasskeyInfo> Passkeys { get; private set; } = [];

    /// <summary>Whether a passkey can be added from the host this request arrived on.</summary>
    public bool PasskeysAvailable { get; private set; }

    /// <summary>Whether the account has a password to prove before adding; a provider account has none.</summary>
    public bool HasPassword { get; private set; }

    /// <summary>The external providers this account signs in through, for an account without a password to prove.</summary>
    public IReadOnlyList<AuthenticationScheme> Providers { get; private set; } = [];

    /// <summary>The relying-party id, for saying which address to come back by; null when none is configured.</summary>
    public string? ServerDomain { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public class InputModel
    {
        [StringLength(NameMaxLength, ErrorMessage = "Passkeys_NameInvalid")]
        [Display(Name = "Passkeys_NameLabel")]
        public string? Name { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "Passkeys_PasswordLabel")]
        public string? Password { get; set; }
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
    /// The first half of adding a passkey: the password proved, creation options for the browser, and
    /// a ceremony started. A POST so the antiforgery token guards it; 404 where passkeys are withheld;
    /// 401 with a message for a wrong password and 429 with one while backed off, which the script
    /// shows in place of the generic cancelled text.
    /// </summary>
    public async Task<IActionResult> OnPostBeginRegistrationAsync(CancellationToken cancellationToken)
    {
        HSUser? user = await _users.GetUserAsync(User);

        if (user is null || !Scheme.Covers(Request.Host))
        {
            return NotFound();
        }

        if (!await _users.HasPasswordAsync(user))
        {
            // No password to prove: the proof is the provider round trip, kept as a ceremony and
            // spent here, so one confirmation adds one passkey.
            PasskeyCeremonies.Outcome proof = _ceremonies.Take(HttpContext, PasskeyCeremonies.ProviderProof);

            if (!proof.Succeeded)
            {
                _logger.LogInformation("Passkey registration refused for user {UserId}: {Reason}.", user.Id, proof.Reason);

                return Refusal(StatusCodes.Status401Unauthorized, _localiser["Passkeys_ProviderNotConfirmed"]);
            }
        }
        else
        {
            // The backoff is checked before the password is compared, so a backed-off session cannot
            // learn whether its guesses were close by watching which refusal comes back.
            DateTimeOffset now = _timeProvider.GetUtcNow();

            if (await _attemptLimiter.RemainingLockoutAsync(user.Id, LimitedAction.AddPasskey, now, cancellationToken)
                    is { } remaining)
            {
                return Refusal(StatusCodes.Status429TooManyRequests,
                               _localiser["Passkeys_PasswordLockedOut", BackoffWait.Format(_localiser, remaining)]);
            }

            if (!await _users.CheckPasswordAsync(user, Input.Password ?? string.Empty))
            {
                await _attemptLimiter.RecordFailedAttemptAsync(user.Id, LimitedAction.AddPasskey, now, cancellationToken);

                _logger.LogInformation("Passkey registration refused for user {UserId}: wrong password.", user.Id);

                return Refusal(StatusCodes.Status401Unauthorized, _localiser["Passkeys_PasswordWrong"]);
            }

            await _attemptLimiter.ResetAsync(user.Id, LimitedAction.AddPasskey, cancellationToken);
        }

        PasskeyCreationOptionsResult creation = await _engine.MakeCreationOptionsAsync(
            new PasskeyUserEntity
            {
                Id = user.Id.ToString(CultureInfo.InvariantCulture),
                Name = user.UserName!,
                DisplayName = user.UserName!,
            },
            HttpContext);

        if (!_ceremonies.Begin(HttpContext, PasskeyCeremonies.Attestation, creation.AttestationState!))
        {
            _logger.LogWarning("Passkey registration refused for user {UserId}: the ceremony ledger is full.", user.Id);

            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        Response.Headers.CacheControl = "no-store";

        return Content(creation.CreationOptionsJson, "application/json; charset=utf-8");
    }

    /// <summary>
    /// For an account without a password: sends the person to re-authenticate at the provider this
    /// account signs in through, asking the provider to make them sign in afresh.
    /// </summary>
    /// <remarks>
    /// <c>max_age=0</c> is the standard's way of saying "now", and makes a conforming provider return
    /// <c>auth_time</c>; <c>prompt=login</c> says the same thing to providers that read that instead.
    /// The external cookie is cleared first so that a stale provider identity cannot be what the
    /// callback finds.
    /// </remarks>
    public async Task<IActionResult> OnPostReauthenticateAsync(string? provider)
    {
        HSUser? user = await _users.GetUserAsync(User);

        if (user is null || string.IsNullOrEmpty(provider))
        {
            return NotFound();
        }

        // A password account proves its password; the provider round trip is for the accounts that
        // have nothing else, and only against a provider the account actually holds a login for.
        if (await _users.HasPasswordAsync(user)
            || (await _users.GetLoginsAsync(user)).All(login => !string.Equals(login.LoginProvider, provider, StringComparison.Ordinal)))
        {
            return NotFound();
        }

        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        string redirectUrl = Url.Page("/Account/Manage/Passkeys", pageHandler: "Reauthenticated")!;
        AuthenticationProperties external = _signInManager.ConfigureExternalAuthenticationProperties(
            provider, redirectUrl, user.Id.ToString(CultureInfo.InvariantCulture));

        OpenIdConnectChallengeProperties challenge = new(external.Items, external.Parameters)
        {
            MaxAge = TimeSpan.Zero,
            Prompt = "login",
        };

        return new ChallengeResult(provider, challenge);
    }

    /// <summary>
    /// The provider's answer: the subject it vouches for must be the one this account signs in with,
    /// and any sign-in time it reports must be recent. Then a proof is started for the registration
    /// to spend.
    /// </summary>
    public async Task<IActionResult> OnGetReauthenticatedAsync()
    {
        HSUser? user = await _users.GetUserAsync(User);

        if (user is null)
        {
            return NotFound();
        }

        // Keyed on the signed-in account, as the account-linking callback is, so a callback carrying
        // somebody else's external cookie is not read as this account's.
        ExternalLoginInfo? info = await _signInManager.GetExternalLoginInfoAsync(user.Id.ToString(CultureInfo.InvariantCulture));

        // Consumed either way: a provider identity is not left lying around for another page to find.
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        if (info is null)
        {
            StatusMessage = _localiser["Passkeys_ProviderFailed"];

            return RedirectToPage();
        }

        IList<UserLoginInfo> logins = await _users.GetLoginsAsync(user);
        string? refusal = ProviderProofRefusal(info, logins, _timeProvider.GetUtcNow());

        if (refusal is not null)
        {
            _logger.LogWarning("Provider re-authentication refused for user {UserId} via {LoginProvider}: {Reason}.",
                               user.Id,
                               info.LoginProvider,
                               refusal);

            StatusMessage = _localiser[refusal == "mismatch" ? "Passkeys_ProviderMismatch" : "Passkeys_ProviderStale", info.ProviderDisplayName ?? info.LoginProvider];

            return RedirectToPage();
        }

        if (!_ceremonies.Begin(HttpContext, PasskeyCeremonies.ProviderProof, info.ProviderKey))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        _logger.LogInformation("User {UserId} re-authenticated at {LoginProvider} to add a passkey.", user.Id, info.LoginProvider);

        StatusMessage = _localiser["Passkeys_ProviderConfirmed", info.ProviderDisplayName ?? info.LoginProvider];

        return RedirectToPage();
    }

    /// <summary>
    /// Why a provider's answer does not count as this account re-authenticating, or
    /// <see langword="null"/> when it does: <c>"mismatch"</c> when the subject is not one this account
    /// signs in with, <c>"stale"</c> when the provider reports a sign-in older than
    /// <see cref="MaxProviderProofAge"/>. A provider that reports no sign-in time is taken at its word.
    /// </summary>
    public static string? ProviderProofRefusal(ExternalLoginInfo info, IEnumerable<UserLoginInfo> logins, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(logins);

        bool held = logins.Any(login => string.Equals(login.LoginProvider, info.LoginProvider, StringComparison.Ordinal)
                                        && string.Equals(login.ProviderKey, info.ProviderKey, StringComparison.Ordinal));

        if (!held)
        {
            return "mismatch";
        }

        string? authTime = info.Principal.FindFirstValue("auth_time");

        if (authTime is not null
            && long.TryParse(authTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
            && now - DateTimeOffset.FromUnixTimeSeconds(seconds) > MaxProviderProofAge)
        {
            return "stale";
        }

        return null;
    }

    /// <summary>
    /// The second half: the browser's attestation, verified against the ceremony started above and
    /// stored under the name given. The password was proved when the ceremony began, and the ceremony
    /// cookie is the proof it was.
    /// </summary>
    public async Task<IActionResult> OnPostRegisterAsync(string? credential)
    {
        HSUser? user = await _users.GetUserAsync(User);

        if (user is null)
        {
            return NotFound();
        }

        await LoadAsync();

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
            _logger.LogInformation("Passkey registration refused for user {UserId}: {Reason}", user.Id, attested.Failure?.Message);

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

        UserPasskeyInfo passkey = attested.Passkey!;
        passkey.Name = string.IsNullOrWhiteSpace(Input.Name)
            ? _localiser["Passkeys_DefaultName", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)].Value
            : Input.Name.Trim();

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

        if (trimmed.Length is 0 or > NameMaxLength)
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

    /// <summary>A refusal the script can show: a status the browser will not follow, and the sentence to display.</summary>
    private static JsonResult Refusal(int status, string message)
    {
        return new JsonResult(new { message }) { StatusCode = status };
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
        HasPassword = await _users.HasPasswordAsync(user);
        ServerDomain = Scheme.IsConfigured ? Scheme.ServerDomain!.Trim() : null;

        if (!HasPassword)
        {
            IList<UserLoginInfo> logins = await _users.GetLoginsAsync(user);

            Providers = (await _signInManager.GetExternalAuthenticationSchemesAsync())
                        .Where(scheme => logins.Any(login => string.Equals(login.LoginProvider, scheme.Name, StringComparison.Ordinal)))
                        .ToList();
        }

        return true;
    }
}
