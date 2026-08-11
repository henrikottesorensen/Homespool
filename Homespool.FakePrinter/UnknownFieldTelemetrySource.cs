using System;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Homespool.FakePrinter;

/// <summary>
/// Wraps another <see cref="ITelemetrySource"/> and adds properties the server does not model, so
/// <c>UnknownFieldTracker</c>'s bounds can be exercised instead of merely reasoned about.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The tracker's own remarks call its path "reachable at wire rate by anyone
/// who can connect", and it defends that with a 64-name cap plus a throttled summary past it - but
/// nothing in the fake could produce an unmodelled field, so <c>unknownFieldOccurrences</c> read 0
/// through every load run to date and both defences had only ever met unit tests.
/// </para>
/// <para>
/// <b>Two shapes, because the threat model has two.</b> <see cref="Distinct"/> false reuses a small
/// fixed set of names, which is the benign case - a firmware release starts reporting something new,
/// and an operator should see it named once and then be left alone. <see cref="Distinct"/> true
/// invents a fresh name every time, which is the hostile case: it is the only thing that drives the
/// distinct-name cap, and past it the tracker must stop learning names and fall back to one summary
/// per window rather than growing memory per message.
/// </para>
/// <para>
/// <b>Rig instrument, not firmware-faithful</b> - mitigation #3 in
/// <c>notes/fake-printer-harness.md</c>. No printer sends these. What is faithful is the *shape* of
/// the hazard: extra properties on an otherwise well-formed message, which is exactly how a firmware
/// upgrade or a hostile client would present.
/// </para>
/// <para>
/// The names deliberately look like plausible telemetry rather than obvious rubbish, so a log line
/// reading them is representative of the real thing an operator would have to judge.
/// </para>
/// </remarks>
public sealed class UnknownFieldTelemetrySource : ITelemetrySource
{
    private readonly ITelemetrySource _inner;

    private int _counter;

    /// <summary>Wraps <paramref name="inner"/>, adding unmodelled properties to what it produces.</summary>
    /// <param name="inner">The source supplying the message these fields are added to.</param>
    public UnknownFieldTelemetrySource(ITelemetrySource inner)
    {
        _inner = inner;
    }

    /// <summary>How many unmodelled properties to add per message. Zero disables the wrapper.</summary>
    public int FieldsPerMessage { get; init; } = 1;

    /// <summary>
    /// True to invent a fresh name for every field of every message - the case that drives the
    /// tracker's distinct-name cap. False to cycle a fixed set, the firmware-upgrade case.
    /// </summary>
    public bool Distinct { get; init; }

    /// <inheritdoc/>
    public byte[]? NextMessage(FakeDevice device)
    {
        byte[]? message = _inner.NextMessage(device);

        if (message is null || FieldsPerMessage <= 0)
        {
            return message;
        }

        return AddFields(message);
    }

    /// <inheritdoc/>
    public TimeSpan DelayBeforeNext(FakeDevice device)
    {
        return _inner.DelayBeforeNext(device);
    }

    /// <summary>
    /// Splices the extra properties in after the opening brace, rather than re-serialising.
    /// </summary>
    /// <remarks>
    /// Textual because the inner source's output is opaque bytes by contract - it may be a capture
    /// replay, which must stay byte-faithful apart from what is deliberately added here. Inserting at
    /// the front keeps the inner message's own field order intact, which matters because that order
    /// was taken from a real capture. A message that is not a non-empty JSON object is passed through
    /// untouched: prepending to <c>{}</c> would produce a trailing comma and invalid JSON, and this
    /// wrapper's job is to add unknown fields, not to send malformed frames - <c>BrokenFrame</c>
    /// covers that separately.
    /// </remarks>
    private byte[] AddFields(byte[] message)
    {
        string json = Encoding.UTF8.GetString(message);

        if (json.Length < 2 || json[0] != '{' || json.TrimEnd().Length < 3)
        {
            return message;
        }

        StringBuilder builder = new();
        builder.Append('{');

        for (int i = 0; i < FieldsPerMessage; i++)
        {
            string name = Distinct ?
                string.Create(CultureInfo.InvariantCulture, $"unmodelled_{Interlocked.Increment(ref _counter)}") :
                string.Create(CultureInfo.InvariantCulture, $"unmodelled_{i}");

            builder.Append('"').Append(name).Append("\":").Append(i).Append(',');
        }

        builder.Append(json.AsSpan(1));

        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}
