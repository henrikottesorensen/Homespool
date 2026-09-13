using System;
using System.Globalization;

using Microsoft.AspNetCore.Authentication;

using Homespool.Model;

namespace Homespool.Host.Authentication;

/// <summary>
/// The refusal a local scheme's failed <see cref="AuthenticateResult"/> carries, in its properties so
/// the page can read it without parsing a message.
/// </summary>
public static class SignInRefusals
{
    private const string Item = "Homespool.SignIn.Refusal";
    private const string RetryAfterItem = "Homespool.SignIn.RetryAfter";

    /// <summary>A failed result carrying <paramref name="refusal"/> and a message for the log.</summary>
    public static AuthenticateResult Fail(SignInRefusal refusal, string message)
    {
        return Fail(refusal, message, retryAfter: null);
    }

    /// <summary>
    /// A refusal that says how long it lasts: a lockout or a backoff with <paramref name="retryAfter"/>
    /// left to run, for a page that tells the person when to try again.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="refusal"/> is <see cref="SignInRefusal.Undefined"/>.</exception>
    public static AuthenticateResult Fail(SignInRefusal refusal, string message, TimeSpan? retryAfter)
    {
        AuthenticationProperties properties = new();
        properties.Items[Item] = refusal.RequireSet().ToString();

        if (retryAfter is { } wait)
        {
            properties.Items[RetryAfterItem] = wait.TotalSeconds.ToString(CultureInfo.InvariantCulture);
        }

        return AuthenticateResult.Fail(message, properties);
    }

    /// <summary>How long the refusal lasts, when it said; <see langword="null"/> when it did not.</summary>
    public static TimeSpan? RetryAfter(this AuthenticateResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Properties?.Items.TryGetValue(RetryAfterItem, out string? value) == true
               && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    /// <summary>
    /// The refusal <paramref name="result"/> carries; <see cref="SignInRefusal.Invalid"/> for a
    /// failure that carries none, and for a result that did not fail at all.
    /// </summary>
    public static SignInRefusal Refusal(this AuthenticateResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // An unset or unknown reason reads as a wrong credential - the refusal that sends nobody
        // anywhere special.
        return result.Properties?.Items.TryGetValue(Item, out string? value) == true
               && Enum.TryParse(value, out SignInRefusal refusal)
               && refusal.IsSet()
            ? refusal
            : SignInRefusal.Invalid;
    }
}
