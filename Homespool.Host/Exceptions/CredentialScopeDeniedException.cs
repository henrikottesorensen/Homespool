using System;

using Homespool.Model;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The credential the request arrived on did not name the capability the action needs - the caller's
/// own rights are not in question.
/// </summary>
/// <remarks>
/// <b>Distinct from <see cref="TeamAccessDeniedException"/> because the two say different things.</b>
/// That one means a team does not permit this account; this one means the account may well permit
/// itself, and the key it used does not. It is what a file operation throws, since files belong to
/// their owner and have no team to refuse on their behalf - the credential is the only gate there is.
/// </remarks>
public class CredentialScopeDeniedException : Exception
{
    public CredentialScopeDeniedException()
        : base("The credential used for this request does not permit it.")
    {
    }

    public CredentialScopeDeniedException(string message)
        : base(message)
    {
    }

    public CredentialScopeDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The refusal naming what was missing, for a log or a diagnostic body.</summary>
    public static CredentialScopeDeniedException For(Capability capability)
    {
        return new CredentialScopeDeniedException(
            $"The credential used for this request does not permit {capability}.");
    }
}
