namespace Homespool.Host.Accounts;

/// <summary>What an administrative act on an account did, or why it did not.</summary>
/// <param name="Refusal">Why nothing happened, or <see cref="UserAdminRefusal.None"/>.</param>
/// <param name="Affected">
/// How many rows the act cleared - tokens revoked, backoffs lifted. Zero on a refusal, and zero on a
/// success that found nothing to clear, which the caller may want to say differently.
/// </param>
public readonly record struct UserAdminResult(UserAdminRefusal Refusal, int Affected = 0)
{
    /// <summary>Whether the act went through.</summary>
    public bool Succeeded => Refusal is UserAdminRefusal.None;

    /// <summary>An act that went through, clearing <paramref name="affected"/> rows.</summary>
    public static UserAdminResult Done(int affected = 0)
    {
        return new UserAdminResult(UserAdminRefusal.None, affected);
    }

    /// <summary>An act that did not happen, for <paramref name="refusal"/>.</summary>
    public static UserAdminResult Refused(UserAdminRefusal refusal)
    {
        return new UserAdminResult(refusal);
    }
}
