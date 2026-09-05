using System;
using System.Buffers.Text;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

using Homespool.Model.Entities;

namespace Homespool.Host.Authentication;

/// <summary>
/// Turns a WebAuthn assertion into the principal of the account whose passkey signed it, and hands
/// out the challenge that assertion has to answer. Nothing more: whether a verified assertion becomes
/// a session is the login page's decision, exactly as it is for a password.
/// </summary>
/// <remarks>
/// <para>
/// <b>A ceremony is two requests, and this handler is both ends of it.</b> A challenge
/// (<see cref="HandleChallengeAsync"/>) answers with the request options the browser passes to
/// <c>navigator.credentials.get</c>, and hands what the server must remember - the challenge bytes
/// and, for a challenge bound to an account, which account - to <see cref="PasskeyCeremonies"/>,
/// which keeps it in a data-protected cookie and spends it once. An authenticate
/// (<see cref="HandleAuthenticateAsync"/>) reads the assertion the browser posted back, takes that
/// state back, and verifies the one against the other. The registration ceremony the Manage page runs
/// goes through the same class, so the two cannot drift in what they protect or how long they last.
/// </para>
/// <para>
/// <b>The verification is the framework's, driven directly.</b> <see cref="IPasskeyHandler{TUser}"/>
/// is the engine: it parses the credential, checks the challenge, the origin, the relying-party id
/// hash, the user-presence and user-verification flags and the signature, and returns the account
/// and its updated credential record without touching a cookie or a sign-in. What it does not do is
/// hold the ceremony state between the two requests - that is <c>SignInManager</c>'s job in the
/// framework, where the state rides in the two-factor cookie and a passkey sign-in runs the
/// first-then-second-factor machinery. <b>This scheme never goes through <c>SignInManager</c></b>, which
/// is what keeps a passkey independent of two-factor authentication rather than an input to it, and a
/// test pins that no production code names its passkey methods.
/// </para>
/// <para>
/// <b>The principal is built by the same factory the sign-in cookie uses</b>, as the token schemes do,
/// so a passkey-authenticated request is indistinguishable downstream from a cookie-authenticated
/// one. It carries <see cref="ClaimTypes.AuthenticationMethod"/> as <see cref="AuthenticationMethod"/>
/// for whoever wants to know how the caller was authenticated, and the ticket's properties name the
/// credential that answered under <see cref="CredentialIdProperty"/>.
/// </para>
/// <para>
/// <b>The relying-party id gates both ends.</b> A challenge from a host the id does not cover answers
/// 404 rather than minting options the browser will refuse, and an assertion is only ever checked
/// against the configured id, never one derived from the request.
/// </para>
/// </remarks>
public sealed class PasskeyAuthenticationHandler : AuthenticationHandler<PasskeyAuthenticationOptions>
{
    /// <summary>The <see cref="ClaimTypes.AuthenticationMethod"/> a passkey-authenticated principal carries.</summary>
    public const string AuthenticationMethod = "passkey";

    /// <summary>
    /// The prefix on every item this scheme and <see cref="PasskeyCeremonies"/> write into
    /// <see cref="AuthenticationProperties"/>, so they read as one family and never collide with
    /// another handler's.
    /// </summary>
    public const string PasskeyPrefix = "Homespool.Passkey";

    /// <summary>
    /// An <see cref="AuthenticationProperties"/> item a challenge may carry to bind the ceremony to one
    /// account: the account's id. Absent, the challenge names no account and the assertion identifies
    /// the account through the credential's user handle - a discoverable credential's sign-in.
    /// </summary>
    public const string UserIdProperty = $"{PasskeyPrefix}.UserId";

    /// <summary>
    /// The ticket property naming the credential that signed, base64url-encoded, for a caller that
    /// wants to say which passkey it was.
    /// </summary>
    public const string CredentialIdProperty = $"{PasskeyPrefix}.CredentialId";

    private readonly IPasskeyHandler<HSUser> _engine;
    private readonly UserManager<HSUser> _users;
    private readonly IUserClaimsPrincipalFactory<HSUser> _claimsFactory;
    private readonly PasskeyCeremonies _ceremonies;

    public PasskeyAuthenticationHandler(IPasskeyHandler<HSUser> engine,
                                        UserManager<HSUser> users,
                                        IUserClaimsPrincipalFactory<HSUser> claimsFactory,
                                        PasskeyCeremonies ceremonies,
                                        IOptionsMonitor<PasskeyAuthenticationOptions> options,
                                        ILoggerFactory loggerFactory,
                                        UrlEncoder encoder)
        : base(options, loggerFactory, encoder)
    {
        _engine = engine;
        _users = users;
        _claimsFactory = claimsFactory;
        _ceremonies = ceremonies;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Answers 200 with the request options as JSON and starts the ceremony. Not a redirect and
    /// not a 401: the caller is a script on a page of ours, asking for a challenge, and there is
    /// nowhere to send it. 404 when passkeys are withheld here - no relying-party id, or a host it does
    /// not cover - because the browser would refuse the ceremony anyway and a refusal now says why.
    /// 503 when the ceremony ledger is full, which is the one answer that means "try again shortly".
    /// </remarks>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        if (!Options.Covers(Request.Host))
        {
            Logger.LogInformation("Passkey challenge withheld: relying-party id {RelyingPartyId} does not cover host {Host}.",
                                  Options.ServerDomain,
                                  Request.Host.Value);

            Response.StatusCode = StatusCodes.Status404NotFound;

            return;
        }

        HSUser? user = null;

        if (properties.Items.TryGetValue(UserIdProperty, out string? userId) && userId is not null)
        {
            user = await _users.FindByIdAsync(userId);

            if (user is null)
            {
                Logger.LogWarning("Passkey challenge withheld: no account has id {UserId}.", userId);

                Response.StatusCode = StatusCodes.Status404NotFound;

                return;
            }
        }

        PasskeyRequestOptionsResult requestOptions = await _engine.MakeRequestOptionsAsync(user, Context);

        if (!_ceremonies.Begin(Context, PasskeyCeremonies.Assertion, requestOptions.AssertionState!))
        {
            // Too many ceremonies in flight to remember another. Not a refusal of this person, so
            // not a 4xx: the box is busy, and a minute later it will not be.
            Logger.LogWarning("Passkey challenge refused: the ceremony ledger is full.");

            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;

            return;
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/json; charset=utf-8";
        Response.Headers.CacheControl = "no-store";

        await Response.WriteAsync(requestOptions.RequestOptionsJson, Context.RequestAborted);

        Logger.LogInformation("Passkey challenge issued for {Audience}.", user is null ? "any account" : $"user {user.Id}");
    }

    /// <inheritdoc/>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? credential = await ReadCredentialAsync();

        // No assertion posted, so this scheme has nothing to say about the request.
        if (credential is null)
        {
            return AuthenticateResult.NoResult();
        }

        if (!Options.Covers(Request.Host))
        {
            Logger.LogInformation("Passkey assertion refused: relying-party id {RelyingPartyId} does not cover host {Host}.",
                                  Options.ServerDomain,
                                  Request.Host.Value);

            return AuthenticateResult.Fail("Passkeys are not available on this host.");
        }

        PasskeyCeremonies.Outcome ceremony = _ceremonies.Take(Context, PasskeyCeremonies.Assertion);

        if (!ceremony.Succeeded)
        {
            Logger.LogInformation("Passkey assertion refused: {Reason}.", ceremony.Reason);

            return AuthenticateResult.Fail($"The passkey ceremony could not be completed: {ceremony.Reason}.");
        }

        PasskeyAssertionResult<HSUser> result = await _engine.PerformAssertionAsync(new PasskeyAssertionContext
        {
            HttpContext = Context,
            CredentialJson = credential,
            AssertionState = ceremony.EngineState,
        });

        if (!result.Succeeded)
        {
            // The engine's reason names which step of the ceremony failed and is worth a log line; the
            // caller gets one refusal for all of them, since every one means "not this passkey".
            Logger.LogInformation("Passkey assertion refused: {Reason}", result.Failure?.Message);

            return AuthenticateResult.Fail("The passkey assertion was refused.");
        }

        HSUser user = result.User!;
        UserPasskeyInfo passkey = result.Passkey!;

        // The engine hands back the credential record with its sign count and backup state moved on,
        // and the ceremony is not complete until that is stored: the sign-count check on the next
        // assertion compares against whatever was written here. Fail closed if it cannot be.
        IdentityResult stored = await _users.AddOrUpdatePasskeyAsync(user, passkey);

        if (!stored.Succeeded)
        {
            Logger.LogError("Passkey assertion refused: the credential record for user {UserId} could not be updated.", user.Id);

            return AuthenticateResult.Fail("The passkey could not be recorded.");
        }

        ClaimsPrincipal principal = await _claimsFactory.CreateAsync(user);

        if (principal.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim(ClaimTypes.AuthenticationMethod, AuthenticationMethod));
        }

        AuthenticationProperties properties = new();
        properties.Items[CredentialIdProperty] = Base64Url.EncodeToString(passkey.CredentialId);

        Logger.LogInformation("Passkey {PasskeyName} authenticated user {UserId}.", passkey.Name ?? "(unnamed)", user.Id);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, properties, Scheme.Name));
    }

    /// <summary>
    /// The assertion this request carries, or <see langword="null"/> when it carries none: the
    /// <see cref="PasskeyAuthenticationOptions.CredentialFormField"/> of a posted form.
    /// </summary>
    private async Task<string?> ReadCredentialAsync()
    {
        if (!HttpMethods.IsPost(Request.Method) || !Request.HasFormContentType)
        {
            return null;
        }

        IFormCollection form = await Request.ReadFormAsync(Context.RequestAborted);

        if (!form.TryGetValue(PasskeyAuthenticationOptions.CredentialFormField, out StringValues values))
        {
            return null;
        }

        string? credential = values.Count == 1 ? values[0] : null;

        return string.IsNullOrWhiteSpace(credential) ? null : credential;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A plain 403. The assertion was good and the answer is still no, so there is no challenge to
    /// issue and no header to name.
    /// </remarks>
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;

        return Task.CompletedTask;
    }
}
