using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The printer has no lighting this application can set: it has never reported a brightness, and its
/// model is not one firmware builds with LED strips.
/// </summary>
/// <remarks>
/// Refused here rather than sent, because the printer's own refusal - <i>"Missing or broken
/// parameters"</i>, from a build that does not know the setting - would tell nobody what happened.
/// </remarks>
public class NoLightingException : Exception, ILocalisableError
{
    /// <summary>The one callers actually use.</summary>
    public NoLightingException(int printerId)
        : base($"Printer {printerId} has no lighting that can be set.")
    {
    }

    // The three constructors every public exception type is expected to carry (CA1032).
    public NoLightingException()
    {
    }

    public NoLightingException(string message)
        : base(message)
    {
    }

    public NoLightingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public string ResourceKey => "Error_NoLighting";

    /// <inheritdoc />
    public object[] ResourceArguments => [];
}
