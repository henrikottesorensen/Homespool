using System;
using System.Globalization;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// The firmware version a printer states about itself, and the one question this project asks of it:
/// whether it can load a custom certificate and therefore reach us over TLS.
/// </summary>
/// <remarks>
/// <para>
/// <b>The answer is a table, not a floor, and that is the whole reason this type exists.</b>
/// <c>custom_cert</c> shipped broken and the fix was cherry-picked into each release branch
/// separately, so the fixed releases are not a contiguous range: 6.4.2 works while 6.4.1 and
/// 6.5.1–6.5.3 do not. The obvious <c>version &gt;= 6.4.2</c> therefore passes three releases that
/// cannot do this at all, which is why <see cref="CanLoadCustomCertificate(string)"/> is written as
/// three clauses and tested on exactly those boundaries.
/// </para>
/// <para>
/// <b>Every claim here was established by reading the firmware at each tag</b>, because ancestry
/// answers it wrongly: a cherry-picked fix exists on a release branch under a different commit, so
/// asking whether the original commit is an ancestor of a tag reports releases as broken that are
/// not. A version this type has never been taught about is treated as incapable, which errs towards
/// saying nothing rather than towards advice that is wrong.
/// </para>
/// <para>
/// <b>Nothing may be gated on this.</b> The version arrives in a header the printer writes about
/// itself, so it is a claim rather than an observation, and the only cost of a false one must be a
/// warning that does or does not appear. Refusing a connection on it would also cut off a printer for
/// having been <i>updated</i>, which is the outcome all of this exists to encourage.
/// </para>
/// </remarks>
public readonly record struct PrinterFirmwareVersion
{
    private PrinterFirmwareVersion(int major, int minor, int patch)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>
    /// Reads a version firmware states, e.g. <c>6.4.0+11974</c> or <c>6.5.7</c>.
    /// </summary>
    /// <remarks>
    /// The build metadata after <c>+</c> is discarded: it identifies a build rather than a release,
    /// and no question asked here turns on it. Anything that does not parse answers false rather than
    /// throwing, because this runs on a string a printer chose and an unreadable one is a printer we
    /// know nothing about, not an error in us.
    /// </remarks>
    public static bool TryParse(string? stated, out PrinterFirmwareVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(stated))
        {
            return false;
        }

        string trimmed = stated.Trim();
        int plus = trimmed.IndexOf('+', StringComparison.Ordinal);

        if (plus >= 0)
        {
            trimmed = trimmed[..plus];
        }

        string[] parts = trimmed.Split('.');

        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
        {
            return false;
        }

        version = new PrinterFirmwareVersion(major, minor, patch);

        return true;
    }

    /// <summary>
    /// Whether firmware stating <paramref name="stated"/> can load a custom certificate, and so could
    /// reach this deployment over TLS rather than through the legacy plaintext listener.
    /// </summary>
    /// <remarks>
    /// False for anything unparseable or unknown, so a printer we cannot place is never told it is
    /// fine.
    /// </remarks>
    public static bool CanLoadCustomCertificate(string? stated)
    {
        return TryParse(stated, out PrinterFirmwareVersion version) && version.CanLoadCustomCertificate();
    }

    /// <summary>
    /// Whether this version can load a custom certificate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three clauses, one per release train that received the fix, and <b>not</b> one comparison:
    /// </para>
    /// <list type="bullet">
    /// <item>6.6.0 and later — every train from there on carries it.</item>
    /// <item>6.5.7 and later within 6.5.x — 6.5.1, 6.5.2 and 6.5.3 predate the fix on that branch.</item>
    /// <item>6.4.2 exactly — the fix release for its train, and the only 6.4.x that has it. Its whole
    /// changelog is <i>"Fixed not loading custom Connect certificates"</i>.</item>
    /// <item>Any major above 6, on the reasoning in the method: this advises and never gates, so the
    /// cost of assuming a future release kept the fix is a dismissible warning, where assuming it did
    /// not is silence.</item>
    /// </list>
    /// <para>
    /// A later 6.4.x would need checking at its own tag before being added here; none exists.
    /// </para>
    /// </remarks>
    public bool CanLoadCustomCertificate()
    {
        if (Major > 6)
        {
            // Forward, not frozen. Nothing above the 6 series has been read at a tag and 7.x does not
            // exist yet, but the fix has been in every shipping release since mid-2026 and a later
            // major carrying it forward is far likelier than a regression.
            //
            // The direction of the error decides this, and the first version got it backwards. This
            // predicate only ever WARNS - it advises against the plaintext listener and prompts a printer
            // to leave it, and gates nothing. So answering false for an unknown future release buys
            // silence exactly where the advice would be right, while answering true costs at worst
            // one confirmation click on a warning somebody can still click through.
            return true;
        }

        if (Major < 6)
        {
            // Below the series that introduced custom_cert at all, so there is nothing to load.
            return false;
        }

        if (Minor >= 6)
        {
            return true;
        }

        if (Minor == 5)
        {
            return Patch >= 7;
        }

        return Minor == 4 && Patch == 2;
    }
}
