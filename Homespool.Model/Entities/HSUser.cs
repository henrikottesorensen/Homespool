using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;

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

    /// <summary>
    /// The ASCII part of <see cref="AllowedUsernameCharacters"/>: Identity's own default set, less
    /// <c>@</c> and <c>+</c>.
    /// </summary>
    public const string AsciiUsernameCharacters =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._";

    /// <summary>
    /// Latin letters left out of <see cref="AllowedUsernameCharacters"/> because Unicode's confusables
    /// table says they look like something else that is in it.
    /// </summary>
    private const string ConfusableLatinLetters =

        // Look like an ASCII letter or digit: wynn ƿ as p, long s ſ and ẝ and ƒ as f, iota Ɩ as l,
        // yr Ʀ as R, turned delta ƍ as g, ỿ as y, and the tone letters, yogh and Ȣ ȣ as digits.
        "ſƄƍƒƖƦƧƷƼƽƿȜȢȣẝỿ"

        // Look like another allowed letter, which stays: the breve, caron and inverted-breve forms of a
        // vowel as each other (ĕ as Czech ě, the pinyin carons as the breves, the Serbo-Croatian
        // accent marks as circumflexes); Ĭ as ľ; ŀ as Ŀ; ḷ as Ị; ẚ as ả; ẛ as ḟ; and the obsolete
        // Zhuang and Lakota letters ƃ ƚ ƞ as Ƃ Ɨ ŋ.
        + "ĔĕĬŀƃƚƞǍǎǏǐǑǒǓǔǦǧȂȃȆȇȊȋȎȏȖȗḷẚẛ"

        // One code point that reads as two letters: the ĳ, ǆ, ǉ, ǌ and ǳ digraphs, the accented æ
        // ligatures, and m with a mark, which reads as rn with a mark. The table only shows ǆ as dž
        // on a second pass, because its mapping carries a caron the table elsewhere folds to a breve.
        + "ĲĳǄǆǇǉǊǌǢǣǱǳǼǽḿṁṃ";

    /// <summary>
    /// The four Latin blocks whose letters are allowed: Latin-1 Supplement from its first letter,
    /// Latin Extended-A, Latin Extended-B and Latin Extended Additional.
    /// </summary>
    /// <remarks>
    /// Declared before <see cref="AllowedUsernameCharacters"/>, and it has to be: static initialisers
    /// run in textual order, and that field's initialiser reads this one. Declared after it, this is
    /// still null when the set is built, and the type fails to initialise.
    /// </remarks>
    private static readonly (int first, int last)[] LatinLetterBlocks =
    [
        (0x00C0, 0x00FF),
        (0x0100, 0x017F),
        (0x0180, 0x024F),
        (0x1E00, 0x1EFF),
    ];

    /// <summary>
    /// Every character a <c>UserName</c> may contain: <see cref="AsciiUsernameCharacters"/>, plus every
    /// upper- and lower-case letter of the Latin script's four main blocks, less the ones Unicode lists
    /// as lookalikes of something else in the set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>@</c> is excluded because sign-in accepts an email address or a username in one field</b>
    /// (<c>Account/Login</c>). Allowing an address-shaped username would let one account occupy
    /// another's address in the sign-in namespace, and the resolution order - username first - is what
    /// would decide who gets the password attempt. Excluding the character makes the two namespaces
    /// disjoint by construction rather than by lookup order. <c>+</c> goes with it: it only ever
    /// appeared here as part of an address.
    /// </para>
    /// <para>
    /// <b>Latin only, and one script by construction.</b> Identity's option is a flat list of characters,
    /// so "any letter" cannot be said; what can be said is every letter of Latin-1 Supplement, Latin
    /// Extended-A, Extended-B and Extended Additional - Søren, Müller, Łukasz and Nguyễn - which is
    /// where the people this is deployed for write their names. A name is therefore never a mix of
    /// scripts, which is what keeps a Cyrillic <c>а</c> out of <c>henrik</c>. Other scripts are not a
    /// refusal in principle; they are a rule this flat list cannot express safely.
    /// </para>
    /// <para>
    /// <b>A letter of a national alphabet in current use is never excluded, whatever the table says.</b>
    /// Unicode's confusables table reads Icelandic <c>þ</c> as <c>p</c> and <c>Ð</c> as the Vietnamese
    /// and Croatian <c>Đ</c>, Romanian <c>ș ț</c> as Turkish <c>ş ţ</c>, Slovak <c>ĺ</c> as <c>Í</c>,
    /// German <c>ẞ</c> as <c>ß</c>, Turkish <c>ı</c> as <c>i</c>, Ewe <c>Ɖ</c> as <c>Đ</c>, Bambara
    /// <c>Ɲ</c> as Latvian <c>Ņ</c>, and <c>Æ Œ</c> as <c>AE OE</c> - by the same reading under which
    /// ASCII <c>rn</c> is <c>m</c>, which this set has always accepted. People are named with these
    /// letters, so all of them stay. That includes dotless <c>ı</c> with its eyes open: Unicode's case
    /// mapping would fold it to <c>I</c> and merge <c>yıldız</c> with <c>yildiz</c>, but .NET's invariant
    /// casing deliberately leaves the Turkish i pair alone, so the normaliser keeps them distinct and
    /// they can be two accounts, like <c>þor</c> and <c>por</c>.
    /// </para>
    /// <para>
    /// <b>Among the rest, no two allowed characters look alike, by that table.</b> Every letter in those
    /// blocks was reduced to its UTS #39 skeleton (<c>confusables.txt</c>, Unicode 17.0), and anything
    /// whose skeleton equals that of an ASCII character, of another allowed letter, or of a run of
    /// allowed letters is left out unless the rule above keeps it: <see cref="ConfusableLatinLetters"/>
    /// lists them by reason. What goes is archaic, phonetic, transliteration and obsolete letters, and
    /// the compatibility digraphs. Adding one back is a one-character edit to that string.
    /// </para>
    /// <para>
    /// Applied by <c>IdentityOptions.User.AllowedUserNameCharacters</c>, so Identity's own
    /// <c>UserValidator</c> is the single place it is enforced - on creation and on every later change
    /// alike. Nothing re-implements the check at the page layer. A name typed with a decomposed accent
    /// - a base letter followed by a combining mark - is refused rather than normalised, because the
    /// combining mark is not in this list; browsers send composed text, so this has not needed
    /// fixing.
    /// </para>
    /// </remarks>
    public static readonly string AllowedUsernameCharacters = BuildAllowedUsernameCharacters();

    private static string BuildAllowedUsernameCharacters()
    {
        StringBuilder characters = new(AsciiUsernameCharacters);

        foreach ((int first, int last) in LatinLetterBlocks)
        {
            for (int codePoint = first; codePoint <= last; codePoint++)
            {
                char letter = (char)codePoint;
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(letter);

                bool cased = category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter;

                if (cased && !ConfusableLatinLetters.Contains(letter))
                {
                    characters.Append(letter);
                }
            }
        }

        return characters.ToString();
    }

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
