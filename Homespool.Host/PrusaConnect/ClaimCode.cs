using System.Text;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Turns what a person typed into the canonical form <see cref="CodeGenerator"/> stores.
/// </summary>
/// <remarks>
/// <para>
/// Registration codes are read off a low-resolution printer LCD and retyped by hand - the printer's
/// QR is hardcoded to Prusa's own servers and cannot be pointed here, so there is no scanning path.
/// This is where that retyping is made forgiving.
/// </para>
/// <para>
/// <b>Applied on the human path only, never to the printer's own poll.</b> The printer echoes back
/// the exact string this server issued, so normalising <c>GET /p/register</c>'s <c>Code</c> header
/// would not help a person and would mask a firmware or transport bug that mangled it.
/// </para>
/// </remarks>
public static class ClaimCode
{
    /// <summary>
    /// Canonicalises a typed registration code: uppercases it, drops the separators people insert,
    /// and applies Crockford's input substitutions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Crockford base32 defines <c>0 = O</c> and <c>1 = I = L</c> <i>on input</i>, which is the whole
    /// reason the alphabet was chosen: a character misread off the screen still resolves to the code
    /// the server issued. These three are exactly the substitutions SimpleBase's own
    /// <c>Base32Alphabet.Crockford</c> declares, against the alphabet
    /// <c>0123456789ABCDEFGHJKMNPQRSTVWXYZ</c>. They are reimplemented here rather than reached
    /// through the library because a decode/re-encode round trip would throw on the malformed input
    /// this method exists to tolerate. <c>U</c> is excluded from the alphabet and has no
    /// substitution, so a typed <c>U</c> is left alone and simply fails to match.
    /// </para>
    /// <para>
    /// <b>Only separators are stripped</b> - spaces, tabs and hyphens, which is what people add when
    /// chunking a code. Anything else unrecognised is kept, so a genuinely wrong code fails to match
    /// rather than being quietly cleaned into something that looks plausible.
    /// </para>
    /// </remarks>
    /// <param name="typed">Whatever arrived from the form. May be null.</param>
    /// <returns>The canonical form, or an empty string for null/empty input.</returns>
    public static string Normalise(string? typed)
    {
        if (string.IsNullOrEmpty(typed))
        {
            return string.Empty;
        }

        StringBuilder normalised = new(typed.Length);

        foreach (char typedCharacter in typed)
        {
            char upper = char.ToUpperInvariant(typedCharacter);

            switch (upper)
            {
                case 'O':
                    normalised.Append('0');
                    break;

                case 'I':
                case 'L':
                    normalised.Append('1');
                    break;

                case '-':
                    break;

                default:
                    if (!char.IsWhiteSpace(upper))
                    {
                        normalised.Append(upper);
                    }

                    break;
            }
        }

        return normalised.ToString();
    }
}
