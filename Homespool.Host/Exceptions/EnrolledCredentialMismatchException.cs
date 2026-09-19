using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// A claimed registration was redeemed for a fingerprint that is enrolled to a different printer than
/// the one the claim names.
/// </summary>
/// <remarks>
/// The claim was made while the fingerprint was not enrolled, so nobody was asked for permission over
/// the printer it has been enrolled to since. No token is issued: handing one out would move that
/// printer's credential to whoever typed the code.
/// </remarks>
public class EnrolledCredentialMismatchException : Exception
{
    public EnrolledCredentialMismatchException()
        : base("The fingerprint behind this registration is enrolled to a different printer than the one it was claimed as.")
    {
    }

    public EnrolledCredentialMismatchException(string message)
        : base(message)
    {
    }

    public EnrolledCredentialMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
