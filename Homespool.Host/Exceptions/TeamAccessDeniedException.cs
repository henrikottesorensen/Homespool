using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The caller lacks the permission a team-scoped action requires - e.g. claiming a printer into a
/// team without <c>CanManage</c> on it, or naming a team the caller is not even a member of.
/// </summary>
public class TeamAccessDeniedException : Exception
{
    public TeamAccessDeniedException()
        : base("The current user does not have the required permission on this team.")
    {
    }

    public TeamAccessDeniedException(string message)
        : base(message)
    {
    }

    public TeamAccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
