using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Authentication;

/// <summary>
/// Authenticates the same personal access token presented as <c>X-Api-Key: hs_&lt;secret&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists for PrusaSlicer</b>, whose print-host client sends this header and nothing else
/// (<c>docs/prusa-slicer-integration.md</c> §2.2). Its only other authentication mode is HTTP digest,
/// which is <em>structurally impossible</em> here: the challenge-response is computed from the secret,
/// and <see cref="ApiTokenService"/> keeps an indexed hash of it and never the secret itself. So this
/// is not a convenience — without it that client cannot authenticate at all.
/// </para>
/// <para>
/// <b>Its own scheme, so a policy can decline it.</b> Nothing is gained against an attacker holding
/// the token, who would simply use <see cref="ApiTokenAuthenticationHandler"/>'s header instead. What
/// it buys is that we do not <em>invite</em> the leakier header anywhere it is not needed:
/// <c>Authorization</c> is redacted by convention in log formats, proxy traces and crash reporters,
/// and <c>X-*</c> is not. Keeping it to the one surface that needs it also stops a compatibility
/// affordance quietly becoming API surface people script against.
/// </para>
/// <para>
/// The header name is OctoPrint's, which is what the client sends; it is not a claim to be OctoPrint.
/// </para>
/// </remarks>
public class XApiKeyAuthenticationHandler : ApiTokenAuthenticationHandlerBase
{
    /// <summary>The header this scheme reads, carrying the token with no scheme in front of it.</summary>
    public const string HeaderName = "X-Api-Key";

    public XApiKeyAuthenticationHandler(ApiTokenService tokens,
                                        UserManager<HSUser> userManager,
                                        IUserClaimsPrincipalFactory<HSUser> claimsFactory,
                                        IOptionsMonitor<ApiTokenAuthenticationSchemeOptions> options,
                                        ILoggerFactory loggerFactory,
                                        UrlEncoder encoder)
        : base(tokens, userManager, claimsFactory, options, loggerFactory, encoder)
    {
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing. There is no registered challenge scheme meaning "put it in a header of my own", and
    /// naming <c>Bearer</c> would advertise a credential this scheme does not read.
    /// <c>Policies.Compat</c> still answers with a complete challenge, because the bearer scheme
    /// paired with it supplies one.
    /// </remarks>
    protected override string? ChallengeScheme => null;

    /// <inheritdoc/>
    protected override string AuthenticationMethod => ApiKeyAuthenticationMethod;

    /// <inheritdoc/>
    protected override string? ReadCredential()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out StringValues apiKey))
        {
            return null;
        }

        // No scheme prefix to strip: the header's whole value is the credential.
        return apiKey.ToString().Trim();
    }
}
