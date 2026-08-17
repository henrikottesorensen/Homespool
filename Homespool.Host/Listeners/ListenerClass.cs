namespace Homespool.Host.Listeners;

/// <summary>
/// Which credential class an endpoint belongs to, and therefore which listener may serve it.
/// </summary>
/// <remarks>
/// A third member — cameras, holding a camera token — is expected rather than hypothetical
/// (<c>notes/camera-support.md</c>), which is why this is an enum keyed on the URL prefix instead of
/// a bool called "printer". Adding it should be a case here and a listener in
/// <see cref="ListenerOptions"/>, not surgery.
/// </remarks>
public enum ListenerClass
{
    Undefined = 0,

    /// <summary>Cookies and personal access tokens: pages, <c>/api</c>, <c>/health</c>.</summary>
    User,

    /// <summary>A printer's fingerprint and token: everything under <c>/p</c>.</summary>
    Printer,

    /// <summary>
    /// A transfer IV, which is the capability: <c>/f/&lt;iv&gt;/raw</c>, the encrypted download a
    /// printer on the pre-websocket transport fetches over a plain HTTP connection of its own. No
    /// other credential, no other route. Its own listener because the body is ciphertext by
    /// construction and the port must be plain HTTP - the printer's TLS door cannot serve it, and
    /// nothing else should share the one door that is deliberately unencrypted.
    /// </summary>
    Transfer,
}
