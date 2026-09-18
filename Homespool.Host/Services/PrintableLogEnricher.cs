using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using Serilog.Core;
using Serilog.Events;

namespace Homespool.Host.Services;

/// <summary>
/// Replaces, in every property of every log event, the characters a reader of a log cannot see or
/// would be acted on by: whatever a sink writes and however it is read afterwards, the value in it
/// is one whose every character shows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why here rather than where a value is logged.</b> A call site can only clean what its author
/// remembered was somebody else's string, and some of the lines that matter are not ours to edit -
/// the request line is written by Serilog's own middleware, with the path as it was decoded. Every
/// one of those is a property by the time it reaches an event, so one enricher sees them all: ours,
/// the framework's, and a sink an operator adds in configuration.
/// </para>
/// <para>
/// <b>Why not leave it to the sink.</b> The JSON formatter escapes the C0 controls and nothing else:
/// <c>DEL</c>, the C1 controls, the bidi and zero-width marks and the line separators reach the file
/// as themselves. And escaping protects only a reader of the raw file - decoding a line to print its
/// message puts every escaped character back. Replacing the character in the event is the one fix
/// that survives both.
/// </para>
/// <para>
/// <b>By Unicode category, which a rule about names could not afford.</b> <c>Control</c>,
/// <c>Format</c>, <c>LineSeparator</c> and <c>ParagraphSeparator</c>, plus a surrogate with no
/// partner. <c>Format</c> takes the joiner out of a family emoji and the Arabic number signs with
/// it; in a log line that is cosmetic, where refusing somebody's file name over one would not be.
/// So this is deliberately wider than <see cref="PrintableText"/>, and never narrower.
/// </para>
/// <para>
/// <b>Replaced with <c>U+FFFD</c>, never dropped</b>, for <see cref="PrintableText"/>'s reason: a
/// value that arrived dirty must not read back as a clean one somebody could have sent.
/// </para>
/// <para>
/// <b>What this does not reach:</b> an exception's text, which is not a property, and the message
/// template, which is ours. It bounds no lengths either.
/// </para>
/// </remarks>
public sealed class PrintableLogEnricher : ILogEventEnricher
{
    private const char Replacement = '\uFFFD';

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        // Collected first: the event's properties cannot be replaced while they are being walked.
        // Null until something has to change, which for almost every event is never.
        List<LogEventProperty>? replaced = null;

        foreach (KeyValuePair<string, LogEventPropertyValue> property in logEvent.Properties)
        {
            LogEventPropertyValue printable = Printable(property.Value);

            if (!ReferenceEquals(printable, property.Value))
            {
                (replaced ??= []).Add(new LogEventProperty(property.Key, printable));
            }
        }

        if (replaced is null)
        {
            return;
        }

        foreach (LogEventProperty property in replaced)
        {
            logEvent.AddOrUpdateProperty(property);
        }
    }

    /// <summary>
    /// <paramref name="text"/> with each unprintable character replaced, or the same string back when
    /// there is nothing to replace.
    /// </summary>
    public static string Printable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        StringBuilder? replaced = null;

        for (int index = 0; index < text.Length;)
        {
            // A surrogate with no partner is not a character at all, and some writers refuse it
            // outright - so it goes the same way as one that is merely invisible.
            bool paired = Rune.TryGetRuneAt(text, index, out Rune rune);
            int length = paired ? rune.Utf16SequenceLength : 1;

            if (paired && !IsUnprintable(rune))
            {
                replaced?.Append(text, index, length);
            }
            else
            {
                replaced ??= new StringBuilder(text.Length).Append(text, 0, index);
                replaced.Append(Replacement);
            }

            index += length;
        }

        return replaced?.ToString() ?? text;
    }

    private static bool IsUnprintable(Rune rune)
    {
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or
                                                UnicodeCategory.Format or
                                                UnicodeCategory.LineSeparator or
                                                UnicodeCategory.ParagraphSeparator;
    }

    /// <summary>The same value back, by reference, unless something inside it had to change.</summary>
    private static LogEventPropertyValue Printable(LogEventPropertyValue value)
    {
        return value switch
        {
            ScalarValue scalar => Printable(scalar),
            SequenceValue sequence => Printable(sequence),
            StructureValue structure => Printable(structure),
            DictionaryValue dictionary => Printable(dictionary),
            _ => value,
        };
    }

    private static ScalarValue Printable(ScalarValue scalar)
    {
        // A value that is not a string is written through its ToString, so that is what is judged -
        // a header collection or a URI carries a stranger's characters as well as any string does.
        // The types that cannot are skipped, which is nearly every property of nearly every event.
        string? text = scalar.Value switch
        {
            null => null,
            string already => already,
            IConvertible or Guid or DateTimeOffset or TimeSpan => null,
            object other => other.ToString(),
        };

        if (text is null)
        {
            return scalar;
        }

        string printable = Printable(text);

        return ReferenceEquals(printable, text) ? scalar : new ScalarValue(printable);
    }

    private static SequenceValue Printable(SequenceValue sequence)
    {
        List<LogEventPropertyValue>? elements = null;

        for (int index = 0; index < sequence.Elements.Count; index++)
        {
            LogEventPropertyValue printable = Printable(sequence.Elements[index]);

            if (!ReferenceEquals(printable, sequence.Elements[index]))
            {
                elements ??= [.. sequence.Elements];
                elements[index] = printable;
            }
        }

        return elements is null ? sequence : new SequenceValue(elements);
    }

    private static StructureValue Printable(StructureValue structure)
    {
        List<LogEventProperty>? properties = null;

        for (int index = 0; index < structure.Properties.Count; index++)
        {
            LogEventPropertyValue printable = Printable(structure.Properties[index].Value);

            if (!ReferenceEquals(printable, structure.Properties[index].Value))
            {
                properties ??= [.. structure.Properties];
                properties[index] = new LogEventProperty(structure.Properties[index].Name, printable);
            }
        }

        return properties is null ? structure : new StructureValue(properties, structure.TypeTag);
    }

    private static DictionaryValue Printable(DictionaryValue dictionary)
    {
        bool changed = false;
        List<KeyValuePair<ScalarValue, LogEventPropertyValue>> entries = new(dictionary.Elements.Count);

        foreach (KeyValuePair<ScalarValue, LogEventPropertyValue> entry in dictionary.Elements)
        {
            ScalarValue key = Printable(entry.Key);
            LogEventPropertyValue value = Printable(entry.Value);

            changed |= !ReferenceEquals(key, entry.Key) || !ReferenceEquals(value, entry.Value);
            entries.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(key, value));
        }

        return changed ? new DictionaryValue(entries) : dictionary;
    }
}
