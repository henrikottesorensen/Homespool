using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// Somebody asked to print again a print that somebody else queued.
/// </summary>
/// <remarks>
/// <b>A correctness refusal rather than a permission one.</b> The file is looked up by name among the
/// caller's own, so going ahead would print whatever <i>they</i> keep under that name - two people on
/// one team having different models under one name is ordinary, and the failure would be silent.
/// </remarks>
public class PrintNotYoursException : Exception, ILocalisableError
{
    public PrintNotYoursException()
        : base("Only whoever queued a print can print it again.")
    {
    }

    public PrintNotYoursException(string message)
        : base(message)
    {
    }

    public PrintNotYoursException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public string ResourceKey => "Printers_ReprintNotYours";

    /// <inheritdoc />
    public object[] ResourceArguments => [];
}
