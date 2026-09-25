using System;

namespace Homespool.Host.Cameras;

/// <summary>
/// What a camera's source looks like on screen, and how a masked one is put back together on the way
/// in. One answer, so every surface hides the same thing.
/// </summary>
/// <remarks>
/// <para>
/// <b>A network camera's source routinely carries its own credential</b> - go2rtc's documentation
/// advertises <c>onvif://user:pass@192.168.1.123:80</c> as the ordinary spelling - and that credential
/// usually opens the camera's own administrative interface, which our permission model has no reach
/// into. Without this the weaker right reads a secret the stronger one writes: the cameras list is
/// visible to anyone holding <c>ViewCamera</c>, while setting a source needs <c>ManageCamera</c>.
/// </para>
/// <para>
/// <b>Two maskings, because the two surfaces have different readers.</b> The list shows the scheme,
/// host and port and nothing else - a viewer needs to know which device this is, and a user name is
/// half a credential. The edit form keeps the user name and hides only the password, because whoever
/// is editing needs to know which account this camera uses, and that form is behind
/// <c>ManageCamera</c> anyway.
/// </para>
/// <para>
/// <b>The list keeps what is safe rather than removing what is secret.</b> A credential is not only
/// ever userinfo: Foscam takes <c>?usr=…&amp;pwd=…</c>, Reolink <c>?user=…&amp;password=…</c>,
/// the older Foscam clones <c>?loginuse=…&amp;loginpas=…</c>, and Xiongmai firmware puts
/// <c>user=…&amp;password=…</c> in the path itself. No list of parameter names is ever complete, and
/// one that misses a camera looks exactly like one that works, so everything after the authority goes.
/// </para>
/// <para>
/// <b>The password survives an edit without being sent to the browser.</b> The form posts the mask
/// back verbatim, so <c>CameraService.UpdateAsync</c> asks this class to put the stored password
/// back before anything validates or saves. Typing a real password replaces it, which is the one case
/// that must keep working.
/// </para>
/// <para>
/// <b>The stored password only goes back into the source it was stored in.</b> Everything except the
/// password has to match, byte for byte. Otherwise whoever can edit a camera could name a server of
/// their own and have the sidecar hand it a password they were never shown - and that password
/// usually opens the camera's own administration, which no permission here covers. A mask that was
/// not restored is left in place, and the caller refuses it rather than storing it.
/// </para>
/// <para>
/// <b>Byte for byte rather than "the same destination".</b> Comparing only the scheme, user, host and
/// port left the rest of the source free, and the rest of the source decides where the sidecar
/// dials: go2rtc reads what follows <c>#</c> as options, and <c>transport=</c> holds an address it
/// connects to instead of the host in front of it, credential and all. Every relaxation of this
/// comparison is a bet on which parts of a source the sidecar treats as an address, and that bet is
/// only visible in somebody else's source tree.
/// </para>
/// <para>
/// <b>String surgery rather than <see cref="Uri"/>.</b> Parsing and re-serialising normalises -
/// lower-casing the host, adding a trailing slash to an empty path, re-encoding escapes - so a source
/// would come back subtly different from the one somebody typed, on every save. The authority is
/// found by hand and every byte outside it is left alone. It also has to cope with sources that are
/// not URLs at all: an attached camera is <c>ffmpeg:device?video=...</c>, which has no authority and
/// falls out of the first check unchanged.
/// </para>
/// </remarks>
public static class CameraSourceDisplay
{
    /// <summary>
    /// Stands in for a password on screen. Invariant and never localised: it is posted back and
    /// compared, so it is read by this class rather than by a person.
    /// </summary>
    public const string HiddenPassword = "****";

    /// <summary>
    /// The scheme, host and port of a source, and nothing else: no user name, password, path, query or
    /// fragment.
    /// </summary>
    /// <remarks>
    /// Two cameras behind one recorder, told apart only by their paths, look the same here. Their
    /// names tell them apart, and the edit form shows the whole source to whoever may change it.
    /// </remarks>
    /// <param name="source">The stored source.</param>
    /// <returns>
    /// The source unchanged when it is not a URL at all - an attached camera's <c>ffmpeg:device</c>
    /// source, which carries no credential.
    /// </returns>
    public static string AddressOnly(string source)
    {
        if (!TryFindAuthority(source, out int start, out int end))
        {
            return source;
        }

        // The last '@' for the same reason TryFindUserInfo gives.
        int at = end > start ? source.LastIndexOf('@', end - 1, end - start) : -1;
        int hostStart = at < 0 ? start : at + 1;

        return string.Concat(source.AsSpan(0, start), source.AsSpan(hostStart, end - hostStart));
    }

    /// <summary>The source with its password replaced by <see cref="HiddenPassword"/>.</summary>
    /// <param name="source">The stored source.</param>
    public static string WithHiddenPassword(string source)
    {
        if (!TryFindUserInfo(source, out int start, out int length))
        {
            return source;
        }

        int separator = source.IndexOf(':', start, length);
        if (separator < 0)
        {
            // A user name and no password: nothing to hide, and nothing to restore later.
            return source;
        }

        int passwordStart = separator + 1;

        return source.Remove(passwordStart, start + length - passwordStart)
                     .Insert(passwordStart, HiddenPassword);
    }

    /// <summary>
    /// Puts the stored password back into a submitted source whose password is still the placeholder,
    /// provided it would go to the same server and account it was stored for.
    /// </summary>
    /// <param name="submitted">What the form posted.</param>
    /// <param name="stored">The source currently held for this camera.</param>
    /// <returns>
    /// <paramref name="submitted"/> unchanged unless it carries the placeholder,
    /// <paramref name="stored"/> has a password to put in its place, and the two are otherwise
    /// identical. An unchanged result can still carry the placeholder - see
    /// <see cref="CarriesHiddenPassword"/>.
    /// </returns>
    public static string RestoreHiddenPassword(string submitted, string stored)
    {
        if (!TryFindUserInfo(submitted, out int start, out int length))
        {
            return submitted;
        }

        int separator = submitted.IndexOf(':', start, length);
        if (separator < 0)
        {
            return submitted;
        }

        int passwordStart = separator + 1;
        int passwordLength = start + length - passwordStart;

        if (!string.Equals(submitted.Substring(passwordStart, passwordLength),
                           HiddenPassword,
                           StringComparison.Ordinal))
        {
            // Somebody typed a real one.
            return submitted;
        }

        if (!TryFindUserInfo(stored, out int storedStart, out int storedLength))
        {
            return submitted;
        }

        int storedSeparator = stored.IndexOf(':', storedStart, storedLength);
        if (storedSeparator < 0)
        {
            return submitted;
        }

        int storedPasswordStart = storedSeparator + 1;
        int storedPasswordLength = storedStart + storedLength - storedPasswordStart;

        if (!SameApartFromThePassword(submitted,
                                      passwordStart,
                                      passwordLength,
                                      stored,
                                      storedPasswordStart,
                                      storedPasswordLength))
        {
            return submitted;
        }

        string storedPassword = stored.Substring(storedPasswordStart, storedPasswordLength);

        return submitted.Remove(passwordStart, passwordLength).Insert(passwordStart, storedPassword);
    }

    /// <summary>Whether a source's password is the placeholder rather than a real one.</summary>
    /// <param name="source">A source, after <see cref="RestoreHiddenPassword"/> has had its chance.</param>
    public static bool CarriesHiddenPassword(string source)
    {
        if (!TryFindUserInfo(source, out int start, out int length))
        {
            return false;
        }

        int separator = source.IndexOf(':', start, length);
        if (separator < 0)
        {
            return false;
        }

        int passwordStart = separator + 1;

        return string.Equals(source.Substring(passwordStart, start + length - passwordStart),
                             HiddenPassword,
                             StringComparison.Ordinal);
    }

    /// <summary>
    /// Separates a source into the address and the credential it carries, so the two can be stored
    /// apart - the address in the clear, the password protected.
    /// </summary>
    /// <param name="source">The source as somebody typed it.</param>
    /// <returns>
    /// The three parts. A source carrying no credential is its own address, with both other parts
    /// null - which is most cameras, and every attached one.
    /// </returns>
    public static CameraSourceParts SplitCredential(string source)
    {
        if (!TryFindUserInfo(source, out int start, out int length))
        {
            return new CameraSourceParts(source, null, null);
        }

        string userInfo = source.Substring(start, length);
        int separator = userInfo.IndexOf(':');
        string address = source.Remove(start, length + 1);

        return separator < 0 ?
            new CameraSourceParts(address, userInfo, null) :
            new CameraSourceParts(address, userInfo[..separator], userInfo[(separator + 1)..]);
    }

    /// <summary>
    /// Puts a credential into an address - the inverse of <see cref="SplitCredential"/>.
    /// </summary>
    /// <remarks>
    /// Serves both readers, which is why it is one method: the sidecar is handed the real password,
    /// and the edit form is handed <see cref="HiddenPassword"/>. Neither caller has to know how a
    /// userinfo component is spelled.
    /// </remarks>
    /// <param name="address">A source with no credential in it.</param>
    /// <param name="user">The user name, or null for no credential at all.</param>
    /// <param name="password">The password, or null for a user name on its own.</param>
    public static string WithCredential(string address, string? user, string? password)
    {
        if (string.IsNullOrEmpty(user))
        {
            return address;
        }

        int scheme = address.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            // Not a URL - an attached camera's ffmpeg:device source. Nothing to put a credential in.
            return address;
        }

        string credential = password is null ? $"{user}@" : $"{user}:{password}@";

        return address.Insert(scheme + 3, credential);
    }

    /// <summary>
    /// Whether two sources are the same but for the password each carries.
    /// </summary>
    /// <remarks>
    /// Compared exactly, including case: a host that differs only in case names the same server, but
    /// letting that through means deciding which other differences are cosmetic too, and the answer
    /// lives in the sidecar's source rather than in this one. Re-typing a password is the cost of
    /// not deciding.
    /// </remarks>
    private static bool SameApartFromThePassword(string submitted,
                                                 int passwordStart,
                                                 int passwordLength,
                                                 string stored,
                                                 int storedPasswordStart,
                                                 int storedPasswordLength)
    {
        return string.Equals(submitted[..passwordStart], stored[..storedPasswordStart], StringComparison.Ordinal) &&
               string.Equals(submitted[(passwordStart + passwordLength)..],
                             stored[(storedPasswordStart + storedPasswordLength)..],
                             StringComparison.Ordinal);
    }

    /// <summary>
    /// Locates the userinfo component - everything between <c>://</c> and the <c>@</c> that ends it.
    /// </summary>
    private static bool TryFindUserInfo(string source, out int start, out int length)
    {
        start = 0;
        length = 0;

        if (!TryFindAuthority(source, out int authorityStart, out int authorityEnd) ||
            authorityEnd <= authorityStart)
        {
            return false;
        }

        // The last '@' rather than the first: a host cannot contain one, but a password in the wild
        // sometimes does without being escaped.
        int at = source.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
        if (at < 0)
        {
            return false;
        }

        start = authorityStart;
        length = at - authorityStart;

        return true;
    }

    /// <summary>
    /// Locates the authority - everything between <c>://</c> and the first <c>/</c>, <c>?</c> or
    /// <c>#</c> after it, which may be nothing at all.
    /// </summary>
    /// <returns>False when the source is not a URL.</returns>
    private static bool TryFindAuthority(string source, out int start, out int end)
    {
        start = 0;
        end = 0;

        if (string.IsNullOrEmpty(source))
        {
            return false;
        }

        int scheme = source.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            return false;
        }

        start = scheme + 3;
        end = source.Length;

        for (int index = start; index < source.Length; index++)
        {
            char character = source[index];

            if (character == '/' || character == '?' || character == '#')
            {
                end = index;
                break;
            }
        }

        return true;
    }
}

/// <summary>A camera source taken apart: the address, and the credential that was in it.</summary>
/// <param name="Address">The source with no credential in it.</param>
/// <param name="User">The user name, or null when the source carried none.</param>
/// <param name="Password">The password, or null when the source carried none.</param>
public sealed record CameraSourceParts(string Address, string? User, string? Password);
