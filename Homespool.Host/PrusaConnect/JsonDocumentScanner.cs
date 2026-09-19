using System;
using System.Buffers;
using System.Text.Json;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Finds where the next JSON object in a byte stream ends, looking at each byte once however the
/// stream is split. It decides where a document ends; the parser still decides whether it is valid.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not just try to parse.</b> A document that is not finished cannot be parsed a piece at a
/// time: <see cref="JsonDocument.TryParseValue"/> starts again from the first byte on every read, so a
/// document of N bytes arriving in reads of f bytes costs about N²/2f bytes of parsing. The sender
/// chooses both numbers - N up to <see cref="PrusaConnectOptions.MaxIncomingMessageBytes"/>, f by
/// sending small frames - and 1 MiB in 1 KiB reads took over two seconds through the read loop.
/// Carrying a <see cref="Utf8JsonReader"/>'s state across reads is not enough either, because the
/// reader cannot stop inside a token: one long string is re-read from its opening quote every time.
/// </para>
/// <para>
/// <b>What it tracks is exactly what can hide a brace:</b> strings, backslash escapes inside them, and
/// the <c>//</c> and <c>/* */</c> comments the reader is configured to skip. Everything else is left
/// to the parse, which runs once on the slice found here. On valid input the two agree on where the
/// document ends; on invalid input the slice is wrong and the parse throws, which is the answer.
/// </para>
/// <para>
/// <b>So a bad token is found at the end, not where it is.</b> <c>{ x x x</c> that never closes is
/// buffered until <see cref="PrusaConnectOptions.MaxIncomingMessageBytes"/> ends the connection,
/// where a parser would have refused it at the first <c>x</c>. That costs nothing a sender could not
/// already spend with valid bytes - an unclosed string buffers to the same limit - and refusing early
/// would take a second pass over every read.
/// </para>
/// <para>
/// <b>The document must be an object.</b> Everything either reference client renders is one, and a
/// top-level number or literal would have no closing byte to find, so it would bring back the cost
/// this type exists to remove.
/// </para>
/// </remarks>
internal sealed class JsonDocumentScanner
{
    /// <summary>Bytes that change anything outside a string: nesting, a string opening, a comment.</summary>
    private static readonly SearchValues<byte> Structural = SearchValues.Create("{}[]\"/"u8);

    /// <summary>Bytes that change anything inside a string: its end, or an escape.</summary>
    private static readonly SearchValues<byte> StringSpecial = SearchValues.Create("\"\\"u8);

    private Mode _mode = Mode.BeforeDocument;

    /// <summary>Where a comment returns to when it ends - before the document or inside it.</summary>
    private Mode _afterComment;

    private int _depth;

    /// <summary>How far into the current document has already been looked at.</summary>
    private long _scanned;

    private enum Mode
    {
        Undefined = 0,
        BeforeDocument = 1,
        Structure = 2,
        InString = 3,
        InStringEscape = 4,
        Slash = 5,
        LineComment = 6,
        BlockComment = 7,
        BlockCommentStar = 8,
    }

    /// <summary>
    /// True while nothing but whitespace and comments has been seen since the last document ended.
    /// A line comment still open counts - the stream ending is what ends it - but a block comment
    /// still open does not, since its <c>*/</c> is missing.
    /// </summary>
    public bool IsBetweenDocuments =>
        _mode == Mode.BeforeDocument || (_mode == Mode.LineComment && _afterComment == Mode.BeforeDocument);

    /// <summary>
    /// Looks at the bytes of <paramref name="buffer"/> not already looked at, and reports whether the
    /// document starting at its first byte has ended.
    /// </summary>
    /// <param name="buffer">The unconsumed bytes, starting where the current document starts. The
    /// same start must be passed on every call until the document ends; only its end may grow.</param>
    /// <param name="length">The document's length in bytes, from the start of
    /// <paramref name="buffer"/>, when this returns true.</param>
    /// <returns>True when the document is complete. The scanner is then ready for the next one.</returns>
    /// <exception cref="JsonException">The document cannot be a printer message: it does not start
    /// with <c>{</c>, or a <c>/</c> starts neither kind of comment.</exception>
    public bool TryFindEnd(ReadOnlySequence<byte> buffer, out long length)
    {
        ReadOnlySequence<byte> unscanned = buffer.Slice(_scanned);

        foreach (ReadOnlyMemory<byte> segment in unscanned)
        {
            ReadOnlySpan<byte> span = segment.Span;
            int i = 0;

            while (i < span.Length)
            {
                // Skip what cannot matter in bulk - the only thing that keeps a long string or a long
                // run of numbers from being a byte-at-a-time loop.
                if (_mode is Mode.InString or Mode.Structure)
                {
                    int next = span[i..].IndexOfAny(_mode == Mode.InString ? StringSpecial : Structural);

                    if (next < 0)
                    {
                        i = span.Length;

                        break;
                    }

                    i += next;
                }

                bool ended = Step(span[i]);

                i++;

                if (ended)
                {
                    length = _scanned + i;
                    Reset();

                    return true;
                }
            }

            _scanned += span.Length;
        }

        length = 0;

        return false;
    }

    /// <summary>Advances the state by one byte. True when that byte closed the document.</summary>
    private bool Step(byte b)
    {
        switch (_mode)
        {
            case Mode.BeforeDocument:
                if (b == (byte)'{')
                {
                    _mode = Mode.Structure;
                    _depth = 1;
                }
                else if (b == (byte)'/')
                {
                    _afterComment = Mode.BeforeDocument;
                    _mode = Mode.Slash;
                }
                else if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                {
                    throw new JsonException("A printer message must be a JSON object.");
                }

                return false;

            case Mode.Structure:
                switch (b)
                {
                    case (byte)'{' or (byte)'[':
                        _depth++;

                        return false;

                    case (byte)'}' or (byte)']':
                        // A mismatched pair, "{]", is counted all the same. The parse finds it.
                        return --_depth == 0;

                    case (byte)'"':
                        _mode = Mode.InString;

                        return false;

                    case (byte)'/':
                        _afterComment = Mode.Structure;
                        _mode = Mode.Slash;

                        return false;

                    default:
                        return false;
                }

            case Mode.InString:
                _mode = b switch
                {
                    (byte)'\\' => Mode.InStringEscape,
                    (byte)'"' => Mode.Structure,
                    _ => Mode.InString,
                };

                return false;

            case Mode.InStringEscape:
                // Whatever follows a backslash is the escape's, a quote included. A \u escape is four
                // hex digits, none of which this type looks at.
                _mode = Mode.InString;

                return false;

            case Mode.Slash:
                _mode = b switch
                {
                    (byte)'/' => Mode.LineComment,
                    (byte)'*' => Mode.BlockComment,
                    _ => throw new JsonException("'/' starts neither a line comment nor a block comment."),
                };

                return false;

            case Mode.LineComment:
                // The reader ends a line comment at either byte, and refuses U+2028 and U+2029 inside
                // one - so ending it here on these two can never put the end after the reader's.
                if (b is (byte)'\n' or (byte)'\r')
                {
                    _mode = _afterComment;
                }

                return false;

            case Mode.BlockComment:
                if (b == (byte)'*')
                {
                    _mode = Mode.BlockCommentStar;
                }

                return false;

            case Mode.BlockCommentStar:
                _mode = b switch
                {
                    (byte)'/' => _afterComment,
                    (byte)'*' => Mode.BlockCommentStar,
                    _ => Mode.BlockComment,
                };

                return false;

            default:
                throw new InvalidOperationException($"Unknown scanner mode {_mode}.");
        }
    }

    private void Reset()
    {
        _mode = Mode.BeforeDocument;
        _afterComment = Mode.Undefined;
        _depth = 0;
        _scanned = 0;
    }
}
