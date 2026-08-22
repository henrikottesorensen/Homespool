namespace Homespool.Host.IntegrationTest;

/// <summary>
/// Where the dex fixture lives and what it is configured with — <b>one home for it</b>, because it
/// had two: the tests knew the issuer and the host, and the skip attribute independently knew the
/// discovery URL, so <c>localhost:5556</c> was written out three times in two files.
/// </summary>
/// <remarks>
/// <para>
/// These still have to agree by hand with <c>dex.yaml</c>, <c>start-dex.sh</c> and <c>stop-dex.sh</c>,
/// which is the honest limit of this: a C# constant cannot be read from YAML or bash. What it does buy
/// is that the C# side moves as one when the fixture does, and that a reader can see the whole
/// contract with the container in one screen rather than deducing it from a probe URL.
/// </para>
/// <para>
/// The port is not configurable. A fixture on a different port is a different fixture, and the scripts
/// hard-code it too — making it a setting would only create a way for the two sides to disagree
/// silently.
/// </para>
/// </remarks>
internal static class DexFixture
{
    /// <summary>Host and port, and what distinguishes "still inside dex" from "handed back to us".</summary>
    public const string Authority = "localhost:5556";

    /// <summary>The issuer, which is what <c>Oidc:Authority</c> is pointed at.</summary>
    public const string Issuer = "http://" + Authority + "/dex";

    /// <summary>
    /// Discovery, which is what both <c>start-dex.sh</c> and <see cref="RequiresDexFactAttribute"/>
    /// wait on — dex binds its listener before it can serve, so the port answering proves nothing.
    /// </summary>
    public const string DiscoveryUrl = Issuer + "/.well-known/openid-configuration";

    /// <summary>The static client in <c>dex.yaml</c>.</summary>
    public const string ClientId = "homespool-test";

    /// <summary>Its secret. A fixture credential, in a file, on purpose — it guards nothing.</summary>
    public const string ClientSecret = "homespool-test-secret";

    /// <summary>
    /// The address dex's <c>mockCallback</c> connector returns, verified against a real id token
    /// rather than taken from documentation. It also asserts <c>email_verified</c> true, which is what
    /// makes the address door reachable at all.
    /// </summary>
    public const string MockEmail = "kilgore@kilgore.trout";
}
