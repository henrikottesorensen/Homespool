using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The account has guessed wrong at registration codes often enough to be backed off, so the code it
/// presented was not looked at.
/// </summary>
public class ClaimLockedOutException : Exception
{
    public ClaimLockedOutException(TimeSpan retryAfter)
        : base($"Too many unknown registration codes have been tried from this account. Try again in {Math.Ceiling(retryAfter.TotalSeconds)} seconds.")
    {
        RetryAfter = retryAfter;
    }

    public ClaimLockedOutException()
    {
    }

    public ClaimLockedOutException(string message)
        : base(message)
    {
    }

    public ClaimLockedOutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>How long until the account may try a code again.</summary>
    public TimeSpan RetryAfter { get; }
}
