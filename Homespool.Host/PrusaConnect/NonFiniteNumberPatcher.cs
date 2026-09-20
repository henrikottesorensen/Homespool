using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Rewrites a printer message that carries <c>nan</c> or <c>inf</c> where a number belongs, so that it
/// can be parsed: each one becomes the quoted literal the serializer reads as that float -
/// <c>"NaN"</c>, <c>"Infinity"</c>, <c>"-Infinity"</c> - and every other byte is left as it arrived.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both reference clients can send one, from a valid gcode file.</b> Firmware reads a file's float
/// metadata with <c>strtod</c> and renders it with <c>"%.*f"</c> (render.cpp, <c>MetaFilter::Float</c>),
/// with no finiteness check between them, so <c>; layer_height = nan</c> reaches the wire as
/// <c>"layer_height":nan</c>. The Python SDK does the same through <c>float()</c> and
/// <c>json.dumps</c>, spelled <c>NaN</c> and <c>Infinity</c>. Neither is JSON, the reader refuses both
/// before any converter or number-handling option is consulted, and a refused message costs the
/// printer its connection - over a number in a file's metadata that nothing here reads.
/// </para>
/// <para>
/// <b>The reader decides where a value was expected; this type only looks at the byte it stopped
/// on.</b> Finding these tokens by scanning would take a second opinion about what is inside a string,
/// free to differ from the parser's. Instead the document is read token by token, and when the reader
/// refuses, the bytes it refused are matched against the grammar below. On a match the reader is
/// handed the quoted literal in their place and carries on after them, which is how a reader is fed
/// a document in pieces anyway. Anything else it refuses is left refused.
/// </para>
/// <para>
/// <b>The grammar is wider than what the two clients send today</b>: an optional sign, then
/// <c>nan</c>, <c>inf</c> or <c>infinity</c> in any ASCII case, then a delimiter. C leaves the choice
/// between <c>inf</c> and <c>infinity</c> to the library, and <c>%F</c> upper-cases both. Widening is
/// free because every spelling of one value becomes the same literal, at a position the reader had
/// already refused as a value.
/// </para>
/// <para>
/// <b>A translation, not a judgement.</b> What a non-finite temperature should mean is not a
/// question about JSON, and this is the one layer that does not know what a temperature is. The
/// value is carried through as the float it names; every inbound DTO is read with options that
/// accept the quoted form (<see cref="DTO.InboundWireJson"/>), and
/// <see cref="Telemetry.FiniteFloat"/> decides what becomes of it where it would be stored -
/// the same place that has to deal with the infinity a valid <c>1e39</c> parses to. A NaN's sign
/// carries nothing and the literal has none, so <c>-nan</c> is <c>"NaN"</c>.
/// </para>
/// <para>
/// <b>Only for a document that has already failed to parse.</b> Every refusal is an exception, so
/// this costs too much to be the first attempt and <see cref="MaxReplacements"/> bounds what a sender
/// can make it cost as the second.
/// </para>
/// </remarks>
public static class NonFiniteNumberPatcher
{
    /// <summary>
    /// The most tokens one message may have replaced. Firmware renders under ten float metadata
    /// fields; past this the message is not a file's metadata gone wrong, and it is refused as it
    /// would have been.
    /// </summary>
    public const int MaxReplacements = 32;

    /// <summary>Longest first, so that <c>inf</c> is not tried against the front of <c>infinity</c>.</summary>
    private static readonly string[] Words = ["infinity", "inf", "nan"];

    /// <summary>
    /// Replaces every non-finite number in <paramref name="document"/> with its quoted literal.
    /// </summary>
    /// <param name="document">One whole document, which has already failed to parse.</param>
    /// <param name="options">The reader options the failed parse used, so that the two agree on
    /// everything else.</param>
    /// <returns>Null when the document is malformed in any way this does not mend, or in this way
    /// more than <see cref="MaxReplacements"/> times. The caller's own exception then stands.</returns>
    public static NonFinitePatch? TryPatch(ReadOnlySpan<byte> document, JsonReaderOptions options)
    {
        List<(int start, int length)> found = [];
        List<NonFiniteToken> replaced = [];

        JsonReaderState state = new(options);
        int offset = 0;
        int strings = 0;

        while (true)
        {
            Utf8JsonReader reader = new(document[offset..], isFinalBlock: true, state);

            // Where the reader stood before the read that threw: that read consumes nothing, so this
            // is the end of the last token it accepted.
            JsonReaderState before = state;
            long consumedBefore = 0;
            bool refused = false;

            try
            {
                while (true)
                {
                    before = reader.CurrentState;
                    consumedBefore = reader.BytesConsumed;

                    if (!reader.Read())
                    {
                        break;
                    }

                    // Values only: a property's name is a token of its own kind.
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        strings++;
                    }
                }
            }
            catch (JsonException)
            {
                refused = true;
            }

            if (!refused)
            {
                break;
            }

            // Between the last accepted token and a value there can only be a colon, a comma and
            // whitespace. A comment there is not looked through: neither client sends one, and the
            // message is then refused as it would have been.
            int gap = offset + (int)consumedBefore;
            int start = gap;

            while (start < document.Length && document[start] is (byte)':' or (byte)',' or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                start++;
            }

            int length = Match(document[start..]);

            if (length == 0 || found.Count == MaxReplacements)
            {
                return null;
            }

            // The separator as it was sent, then the literal, as a block that is not the last: the
            // reader applies its own rules to whether a value may stand here, and refuses if not.
            byte[] bridge = [.. document[gap..start], .. LiteralFor(document.Slice(start, length))];
            Utf8JsonReader bridgeReader = new(bridge, isFinalBlock: false, before);

            try
            {
                while (bridgeReader.Read())
                {
                }
            }
            catch (JsonException)
            {
                return null;
            }

            if (bridgeReader.TokenType != JsonTokenType.String || bridgeReader.BytesConsumed != bridge.Length)
            {
                return null;
            }

            found.Add((start, length));
            replaced.Add(new NonFiniteToken(strings, Encoding.ASCII.GetString(document.Slice(start, length))));
            strings++;

            state = bridgeReader.CurrentState;
            offset = start + length;
        }

        if (found.Count == 0)
        {
            return null;
        }

        return new NonFinitePatch(Replace(document, found), replaced);
    }

    /// <summary>
    /// The length of the non-finite number <paramref name="bytes"/> starts with, or zero. It has to
    /// end at a delimiter or at the end of the document, so <c>nanx</c> and <c>infinit</c> are nothing.
    /// </summary>
    private static int Match(ReadOnlySpan<byte> bytes)
    {
        int sign = bytes.Length > 0 && bytes[0] is (byte)'-' or (byte)'+' ? 1 : 0;

        foreach (string word in Words)
        {
            int end = sign + word.Length;

            if (bytes.Length < end || !Ascii.EqualsIgnoreCase(bytes[sign..end], word))
            {
                continue;
            }

            if (end == bytes.Length || bytes[end] is (byte)',' or (byte)'}' or (byte)']' or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                return end;
            }
        }

        return 0;
    }

    /// <summary>
    /// The literal for a matched token, quotes included: the only three the serializer reads.
    /// </summary>
    private static ReadOnlySpan<byte> LiteralFor(ReadOnlySpan<byte> token)
    {
        bool negative = token[0] == (byte)'-';
        bool signed = negative || token[0] == (byte)'+';

        if (token[signed ? 1 : 0] is (byte)'n' or (byte)'N')
        {
            return "\"NaN\""u8;
        }

        return negative ? "\"-Infinity\""u8 : "\"Infinity\""u8;
    }

    private static byte[] Replace(ReadOnlySpan<byte> document, List<(int start, int length)> found)
    {
        int size = document.Length;

        foreach ((int start, int length) in found)
        {
            size += LiteralFor(document.Slice(start, length)).Length - length;
        }

        byte[] result = new byte[size];
        int read = 0;
        int written = 0;

        foreach ((int start, int length) in found)
        {
            document[read..start].CopyTo(result.AsSpan(written));
            written += start - read;

            ReadOnlySpan<byte> literal = LiteralFor(document.Slice(start, length));

            literal.CopyTo(result.AsSpan(written));
            written += literal.Length;

            read = start + length;
        }

        document[read..].CopyTo(result.AsSpan(written));

        return result;
    }
}
