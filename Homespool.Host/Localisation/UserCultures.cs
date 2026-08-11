using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;

namespace Homespool.Host.Localisation;

/// <summary>
/// The language an account reads, answerable without a request in flight.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the piece the whole of Phase A exists for.</b> Alerts and invitations are written by
/// hosted services — <c>TelemetryAlertService</c> runs on a timer and owns no
/// <c>HttpContext</c> — so there is no <c>Accept-Language</c> to read and no ambient culture worth
/// having. Without something like this, every email a deployment sends is in whatever language the
/// server happens to be configured for, regardless of who receives it.
/// </para>
/// <para>
/// <b>It reads the stored column only, and deliberately has no other source.</b> A cookie belongs
/// to a browser, and the recipient of an alert may not have one open. That is the argument for
/// <c>HSUser.Language</c> being persisted at all rather than being a request concern, and it is why
/// the column landed before any string was translated.
/// </para>
/// <para>
/// Null from <see cref="ForUserAsync"/> means "nobody chose" rather than "English". The caller
/// decides what to do about it — for email that means the deployment default, since there is no
/// browser to follow.
/// </para>
/// </remarks>
public sealed class UserCultures
{
    private readonly HomespoolDbContext _context;

    public UserCultures(HomespoolDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Runs an action as though the request had arrived in this culture, and puts it back after.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For composing an email, not for handling a request</b> — the middleware already does this
    /// for anything with an <c>HttpContext</c>. A background sender has to set it itself, and has to
    /// restore it, because the thread is the host's and the next thing to run on it did not ask to
    /// be Danish.
    /// </para>
    /// <para>
    /// Both <c>CurrentCulture</c> and <c>CurrentUICulture</c> are set: the first decides how a date
    /// and a number render inside the message, the second which resource file its sentences come
    /// from. Setting only one produces an email written in Danish with American dates in it.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">What the action produces - a composed message, usually.</typeparam>
    /// <param name="cultureName">The culture to run in; anything unsupported runs unchanged.</param>
    /// <param name="action">The work to do in that culture.</param>
    public static T InCulture<T>(string? cultureName, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        string? resolved = SupportedLanguages.Resolve(cultureName);
        if (resolved is null)
        {
            return action();
        }

        CultureInfo culture = CultureInfo.GetCultureInfo(resolved);
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    /// <summary>
    /// The culture an account chose, or null when it has not chosen one.
    /// </summary>
    /// <remarks>
    /// A stored value that no longer names a shipped language answers null rather than throwing:
    /// dropping a translation should degrade to the default, not break every email to whoever had
    /// selected it.
    /// </remarks>
    public async Task<string?> ForUserAsync(long userId, CancellationToken cancellationToken)
    {
        string? stored = await _context.Users
                                       .Where(user => user.Id == userId)
                                       .Select(user => user.Language)
                                       .FirstOrDefaultAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return SupportedLanguages.Resolve(stored);
    }

    /// <summary>
    /// The culture to write to an address, or null when nobody there has chosen one.
    /// </summary>
    /// <remarks>
    /// By address because that is what a sender has: <c>IEmailSender</c> takes a recipient, not a
    /// user id, and the alert path does not look an account up to send to it. Matched on the
    /// normalised address so casing cannot decide somebody's language.
    /// </remarks>
    public async Task<string?> ForEmailAsync(string emailAddress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emailAddress);

        string normalised = emailAddress.ToUpperInvariant();

        string? stored = await _context.Users
                                       .Where(user => user.NormalizedEmail == normalised)
                                       .Select(user => user.Language)
                                       .FirstOrDefaultAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return SupportedLanguages.Resolve(stored);
    }
}
