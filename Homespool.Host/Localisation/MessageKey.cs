namespace Homespool.Host.Localisation;

/// <summary>
/// A sentence a service has decided on but not yet said — the key, and what fills its holes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same trade as <see cref="Exceptions.ILocalisableError"/>, for results rather than
/// throws.</b> A service that decides <i>which</i> sentence a page should show has no business
/// deciding <i>in what language</i>: it may be running on a timer, on a background queue, or for a
/// request whose culture is not the reader's. So the decision travels as a key and the rendering
/// happens where the localiser is.
/// </para>
/// <para>
/// <b>It exists because six localisation audits missed the strings it replaces.</b>
/// <c>CameraService</c> and <c>CameraSourcePolicy</c> wrote finished English sentences into their
/// return values, which <c>Pages/Cameras/Index</c> then rendered verbatim. Nothing in the page said
/// so, and nothing in the service looked like UI, which is exactly why it survived every search
/// aimed at one or the other.
/// </para>
/// </remarks>
/// <param name="Key">The resource key.</param>
/// <param name="Arguments">
/// What fills the sentence's holes. May contain text this application did not author — an address
/// somebody typed, a URI scheme — which is reproduced rather than translated.
/// </param>
public sealed record MessageKey(string Key, object[] Arguments)
{
    /// <summary>A key, with whatever arguments its sentence takes.</summary>
    public static MessageKey For(string key, params object[] arguments)
    {
        return new MessageKey(key, arguments);
    }
}
