namespace Homespool.Host.Http;

/// <summary>
/// The scheme word in front of the credential in an <c>Authorization</c> header or a
/// <c>WWW-Authenticate</c> challenge. Not authentication scheme <em>names</em>; those are in
/// <c>Schemes</c>.
/// </summary>
public static class AuthorizationSchemes
{
    /// <summary>RFC 7617: <c>base64(user:password)</c>.</summary>
    public const string Basic = "Basic";

    /// <summary>RFC 6750: a bearer token, here a personal access token.</summary>
    public const string Bearer = "Bearer";
}
