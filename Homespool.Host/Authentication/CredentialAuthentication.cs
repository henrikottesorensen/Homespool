using System;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace Homespool.Host.Authentication;

/// <summary>
/// How a page presents a credential it collected to a scheme it demands by name. The one channel
/// <c>AuthenticateAsync(scheme)</c> leaves open is the request's feature collection, so the credential
/// goes in there, keyed by its own type, and the handler reads it back with
/// <c>Context.Features.Get&lt;T&gt;()</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The page binds; the scheme verifies.</b> A scheme that read the posted form by field name would
/// reach around model binding, antiforgery and validation, and would pick up a <c>password</c> field
/// on any page that happened to have one. Presenting the credential explicitly keeps all three where
/// they belong and makes the call say what was collected.
/// </para>
/// <para>
/// <b>Presenting nothing is not an attempt.</b> A scheme that finds no credential of its type reports
/// <c>NoResult</c>, never a refusal. Several credentials may be presented at once; each scheme finds
/// its own and ignores the rest.
/// </para>
/// <para>
/// <b>Named apart from the framework's <c>AuthenticateAsync</c> on purpose.</b> An extension with a
/// <c>params</c> tail is applicable to the two-argument call too, and inside this namespace it would
/// be found before the framework's, so an overload of the same name called itself until the stack
/// ran out. The test process died of it once; the name is the fix.
/// </para>
/// </remarks>
public static class CredentialAuthentication
{
    /// <summary>
    /// Presents <paramref name="credentials"/> to the request and authenticates <paramref name="scheme"/>
    /// over it, returning the scheme's ticket or refusal.
    /// </summary>
    public static Task<AuthenticateResult> AuthenticateWithAsync(this HttpContext context, string scheme, params object[] credentials)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(credentials);

        foreach (object credential in credentials)
        {
            ArgumentNullException.ThrowIfNull(credential, nameof(credentials));

            context.Features[credential.GetType()] = credential;
        }

        return context.AuthenticateAsync(scheme);
    }
}
