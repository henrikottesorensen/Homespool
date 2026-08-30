using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The printer has not been set up to be marked ready from anywhere but the machine itself -
/// <c>Printer.RemoteReadyAllowed</c> is off.
/// </summary>
/// <remarks>
/// <para>
/// <b>A refusal about the printer, not about the caller</b>, which is why it is not a
/// <see cref="TeamAccessDeniedException"/>. The caller may well hold every capability there is; the
/// machine has simply been told that nobody asserts its sheet is clear from a screen. Answering
/// "forbidden" is still the right status, because the distinction that matters to a caller is that
/// no permission they could be granted would change the answer - only a manager turning the toggle
/// on would.
/// </para>
/// <para>
/// Carries the printer id for the log rather than for the reader: the sentence a reader sees names
/// no printer, because they are looking at one when they see it.
/// </para>
/// </remarks>
public class RemoteReadyNotAllowedException : Exception, ILocalisableError
{
    public RemoteReadyNotAllowedException()
    {
    }

    public RemoteReadyNotAllowedException(string message)
        : base(message)
    {
    }

    public RemoteReadyNotAllowedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RemoteReadyNotAllowedException(int printerId)
        : base($"Printer {printerId} is not set up to be marked ready remotely.")
    {
        PrinterId = printerId;
    }

    /// <summary>The printer that refused it.</summary>
    public int PrinterId { get; }

    /// <inheritdoc />
    public string ResourceKey => "Error_RemoteReadyNotAllowed";

    /// <inheritdoc />
    public object[] ResourceArguments => [];
}
