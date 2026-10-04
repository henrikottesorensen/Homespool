using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Homespool.Host.Pages;

/// <summary>
/// A page that reports what its last post did: a message, and what kind of news it is, carried
/// across the redirect in TempData.
/// </summary>
/// <remarks>
/// <para>
/// <b>The kind is stored as a nullable <see cref="int"/> and read as a <see cref="StatusKind"/>,
/// and neither half of that can be simplified away.</b> TempData's serializer writes an enum as a
/// number and hands back an <see cref="int"/>, which reflection will assign to an enum property but
/// not to a nullable one: a <c>[TempData] StatusKind?</c> answers 500 on the page after the
/// redirect. A plain <c>[TempData] StatusKind</c> round-trips, but its value is never null, so the
/// filter writes it back on every response whether or not anything set it - a TempData cookie on
/// responses that must set none, such as the one showing freshly minted recovery codes.
/// </para>
/// <para>
/// <b>Key names are shared across pages, and that is how a message reaches the page it is shown
/// on.</b> <c>Disable2fa</c> says what it did on <c>TwoFactorAuthentication</c>, which only works
/// because both read <c>StatusMessage</c> and <c>StatusMessageKind</c> under the same keys.
/// </para>
/// </remarks>
public abstract class StatusMessagePageModel : PageModel
{
    /// <summary>What the last post did, already localised.</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>
    /// What <see cref="StatusMessage"/> reports. <see cref="StatusKind.Undefined"/> when nothing set
    /// it, which is shown as a warning, and is never stored.
    /// </summary>
    public StatusKind StatusMessageKind
    {
        get => (StatusKind)(StoredStatusMessageKind ?? (int)StatusKind.Undefined);
        set => StoredStatusMessageKind = value == StatusKind.Undefined ? null : (int)value;
    }

    /// <summary>
    /// <see cref="StatusMessageKind"/> as TempData holds it. Public only because TempData binds
    /// nothing else; read and write the kind through <see cref="StatusMessageKind"/>.
    /// </summary>
    [TempData(Key = nameof(StatusMessageKind))]
    public int? StoredStatusMessageKind { get; set; }
}
