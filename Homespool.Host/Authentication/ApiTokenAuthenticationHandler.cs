using System;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Authentication;

/// <summary>
/// Authenticates <c>Authorization: Bearer hs_&lt;secret&gt;</c> — the native way to present a personal
/// access token, accepted everywhere <c>/api/v1</c> is.
/// </summary>
/// <remarks>
/// The resolution is <see cref="ApiTokenAuthenticationHandlerBase"/>'s; this class is the header.
/// <see cref="XApiKeyAuthenticationHandler"/> is the same token in the header one client we do not
/// control insists on, deliberately scoped to the surface that client talks to.
/// </remarks>
public class ApiTokenAuthenticationHandler : ApiTokenAuthenticationHandlerBase
{
    public const string BearerSchemePrefix = "Bearer";

    public ApiTokenAuthenticationHandler(ApiTokenService tokens,
                                         UserManager<HSUser> userManager,
                                         IUserClaimsPrincipalFactory<HSUser> claimsFactory,
                                         IOptions<Services.SecurityOptions> security,
                                         IOptionsMonitor<ApiTokenAuthenticationSchemeOptions> options,
                                         ILoggerFactory loggerFactory,
                                         UrlEncoder encoder)
        : base(tokens, userManager, claimsFactory, security, options, loggerFactory, encoder)
    {
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>Bearer</c> is what RFC 9110 wants named on a 401, and no browser turns it into a credential
    /// dialog — only <c>Basic</c> and <c>Digest</c> do that.
    /// </remarks>
    protected override string? ChallengeScheme => BearerSchemePrefix;

    /// <inheritdoc/>
    protected override string AuthenticationMethod => BearerAuthenticationMethod;

    /// <inheritdoc/>
    protected override string? ReadCredential()
    {
        if (!Request.Headers.TryGetValue(HeaderNames.Authorization, out StringValues authorization))
        {
            return null;
        }

        string header = authorization.ToString();

        // Some other scheme's credential. The scheme name is case-insensitive on the wire.
        if (!header.StartsWith($"{BearerSchemePrefix} ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return header[BearerSchemePrefix.Length..].Trim();
    }
}
