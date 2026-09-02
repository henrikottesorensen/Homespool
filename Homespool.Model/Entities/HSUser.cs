using System;
using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Identity;

namespace Homespool.Model.Entities;

public class HSUser : IdentityUser<long>
{
    /// <summary>
    /// The maximum length of a <c>UserName</c>. Long enough for a real name, short enough that a
    /// header greeting cannot be used to deface a page.
    /// </summary>
    public const int UsernameMaxLength = 64;

    /// <summary>
    /// The maximum length of <see cref="Language"/>. Long enough for the longest culture name that
    /// could reasonably be offered (<c>zh-Hant-TW</c>), short enough to be obviously not free text.
    /// </summary>
    public const int LanguageMaxLength = 16;

    public HSUser()
    {
        SecurityStamp = Guid.NewGuid().ToString();
    }

    /// <summary>
    /// The language this account reads Homespool in, as a culture name (<c>en</c>, <c>da</c>), or
    /// null to follow whatever the browser asks for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null is not "English".</b> It means nobody has chosen, so the browser decides and the
    /// choice follows a person who changes their system language. A user who picked English gets
    /// <c>en</c> stored and stops following the browser, which is a different thing and has to stay
    /// distinguishable - the same argument <see cref="Printer.Name"/> and <c>Camera.Name</c> both
    /// make for staying null until somebody types something.
    /// </para>
    /// <para>
    /// <b>Persisted rather than left to the request, because the request is not always there.</b>
    /// Alerts and invitations are sent from hosted services with no <c>Accept-Language</c> to read -
    /// see <c>TelemetryAlertService</c> - so a preference that lived only in a cookie would mean
    /// every email fell back to the server's culture. That is the whole reason this column exists
    /// ahead of any string being translated.
    /// </para>
    /// <para>
    /// A culture name rather than an enum: the set of shipped languages is configuration, and an
    /// enum would put it in the schema, where adding one is a migration.
    /// </para>
    /// </remarks>
    [MaxLength(LanguageMaxLength)]
    public string? Language { get; set; }

    /// <summary>
    /// The <see cref="Printer"/> this account reaches for when a page has to pick one, or null when
    /// nobody has chosen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null is not "the first printer".</b> It means no choice has been made, and the pages that
    /// read this leave their selection empty rather than aiming at whichever machine sorts first - a
    /// guess presented as a choice is how a print reaches a printer nobody meant, with every layer
    /// below reporting success because both destinations were legal.
    /// </para>
    /// <para>
    /// <b>Per account rather than per printer.</b> A printer belongs to a team and several people
    /// share it; which one you reach for first is a fact about you, exactly as
    /// <see cref="Language"/> is.
    /// </para>
    /// <para>
    /// <b>A plain id, not a foreign key</b>, on the same reasoning as <see cref="Team.CreatedBy"/>:
    /// it keeps a preference from entangling itself with printer lifetime, so removing a printer or
    /// dropping somebody from a team needs no step here. Whoever reads this resolves it against the
    /// printers the account can actually see, and an id that no longer answers means no default.
    /// <b>That is only safe because the key is <c>AUTOINCREMENT</c></b> and a removed printer's id
    /// is never handed to a new machine; over a reusing key this column would silently retarget.
    /// </para>
    /// </remarks>
    public int? DefaultPrinterId { get; set; }

    public HSUser(string userName)
        : this()
    {
        UserName = userName;
    }
}
