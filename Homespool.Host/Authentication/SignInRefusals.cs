using System;

using Microsoft.AspNetCore.Authentication;

namespace Homespool.Host.Authentication;

/// <summary>
/// The refusal a local scheme's failed <see cref="AuthenticateResult"/> carries, in its properties so
/// the page can read it without parsing a message.
/// </summary>
public static class SignInRefusals
{
    private const string Item = "Homespool.SignIn.Refusal";

    /// <summary>A failed result carrying <paramref name="refusal"/> and a message for the log.</summary>
    public static AuthenticateResult Fail(SignInRefusal refusal, string message)
    {
        AuthenticationProperties properties = new();
        properties.Items[Item] = refusal.ToString();

        return AuthenticateResult.Fail(message, properties);
    }

    /// <summary>
    /// The refusal <paramref name="result"/> carries; <see cref="SignInRefusal.Invalid"/> for a
    /// failure that carries none, and for a result that did not fail at all.
    /// </summary>
    public static SignInRefusal Refusal(this AuthenticateResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Properties?.Items.TryGetValue(Item, out string? value) == true
               && Enum.TryParse(value, out SignInRefusal refusal)
            ? refusal
            : SignInRefusal.Invalid;
    }
}
