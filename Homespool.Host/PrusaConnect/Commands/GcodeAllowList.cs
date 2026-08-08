using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// The only gcode this application will send. Everything gcode-shaped passes through here, including
/// commands the application composes itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an allowlist and not a filter.</b> <see cref="GCode"/> explains the chain this exists to
/// break: firmware's <c>M997</c> reflashes the mainboard from a <c>.bbf</c> under <c>/usb/</c>,
/// validating nothing, so "upload a file" plus "send arbitrary gcode" is arbitrary firmware on
/// someone's printer. Its own conclusion was that the safe shape is an allowlist rather than a
/// passthrough, and that <c>M997</c> is the first entry that must not be on it.
/// </para>
/// <para>
/// <b>It is a chokepoint, not a gate on user input.</b> Nothing accepts gcode from a caller today -
/// the only callers are typed commands that compose their own line from a number. Routing those
/// through here anyway is the point: a defect in a future typed command still cannot emit something
/// off this list. The alternative, trusting composed strings because they came from us, is exactly
/// how the barrier erodes.
/// </para>
/// <para>
/// <b>Deny by shape, not by prefix.</b> Each entry is a whole-line pattern with its argument range
/// checked, so <c>M104 S215</c> passes and <c>M104 S215 M997</c>, <c>M1040</c> and
/// <c>N1 M104 S215*42</c> do not. Prefix matching is how allowlists like this usually fail.
/// </para>
/// <para>
/// <b>A frame may carry several lines, and every one of them is checked.</b> Firmware walks a gcode
/// body line by line - <c>memchr</c> for the newline, execute, advance, repeat
/// (<c>src/connect/background.cpp:21-87</c>) - so one command can set both heaters, which is the
/// only way to do it atomically: two commands race, because gcode is answered <c>Accepted</c> when
/// it is queued and the second arrives while the first is still running.
/// </para>
/// <para>
/// So the rule is <b>every line must be permitted</b>, not <b>there must be one line</b>. The
/// guarantee is unchanged - <c>M997</c> is on no line - and this states the actual intent, which was
/// never that newlines are dangerous but that an unvetted second command is. A separator is only a
/// smuggling vector when what follows it goes unchecked.
/// </para>
/// </remarks>
public static class GcodeAllowList
{
    /// <summary>
    /// Hottest nozzle target that may be sent. Above every preset in firmware's own table (the
    /// highest is PA at 285) and below the point where firmware would refuse it anyway.
    /// </summary>
    public const int MaxNozzleTemperature = 300;

    /// <summary>Hottest bed target that may be sent. Firmware's presets top out at 100.</summary>
    public const int MaxBedTemperature = 120;

    /// <summary>
    /// Most lines one frame may carry. Two is what setting both heaters needs; the cap exists so a
    /// composer cannot batch, and so a body stays far below the size at which firmware answers
    /// <c>GcodeTooLarge</c>.
    /// </summary>
    public const int MaxLines = 4;

    /// <summary>
    /// <c>M997</c>, named rather than merely absent.
    /// </summary>
    /// <remarks>
    /// Absence would be enough today. This is here so that the <em>intent</em> survives someone
    /// widening the list in a hurry: a test asserts this specific line is refused, so a change that
    /// would admit it fails with a message that says why rather than going quietly green.
    /// </remarks>
    public const string ReflashCommand = "M997";

    private static readonly Regex NozzleTarget =
        new(@"^M104 S(?<temperature>0|[1-9][0-9]{0,2})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex BedTarget =
        new(@"^M140 S(?<temperature>0|[1-9][0-9]{0,2})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Whether <paramref name="body"/> is a gcode body this application is permitted to send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A body may hold up to <see cref="MaxLines"/> newline-separated lines, and <b>every one</b>
    /// must be permitted. One rejected line rejects the whole body: a frame is executed as a unit,
    /// so admitting it partly would mean admitting it entirely.
    /// </para>
    /// <para>
    /// Comments, checksums and line numbers are <b>refused rather than normalised</b>. Unlike a
    /// newline they have no legitimate use here, so there is nothing to weigh against the fact that
    /// each is a way to hide a second command from a matcher.
    /// </para>
    /// </remarks>
    public static bool IsAllowed(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        // \r is not a separator firmware recognises - it walks on \n alone - so a stray one would
        // ride along inside a line and break the match. Refused rather than stripped.
        if (body.IndexOfAny([';', '(', '*', '\r']) >= 0)
        {
            return false;
        }

        string[] lines = body.Trim().Split('\n');

        if (lines.Length > MaxLines)
        {
            return false;
        }

        foreach (string line in lines)
        {
            string candidate = line.Trim();

            if (!Matches(NozzleTarget, candidate, MaxNozzleTemperature)
                && !Matches(BedTarget, candidate, MaxBedTemperature))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Every line the application may send, for tests and for a reader wanting the whole list.</summary>
    public static IReadOnlyList<string> Describe()
    {
        return
        [
            $"M104 S<0-{MaxNozzleTemperature}> — nozzle target temperature",
            $"M140 S<0-{MaxBedTemperature}> — bed target temperature",
        ];
    }

    private static bool Matches(Regex pattern, string candidate, int maximum)
    {
        Match match = pattern.Match(candidate);

        if (!match.Success)
        {
            return false;
        }

        // The pattern bounds this to three digits, so it cannot overflow - but the range is a
        // separate question from the shape, and a temperature above the maximum is refused here
        // rather than sent for the printer to reject.
        return int.Parse(match.Groups["temperature"].Value, CultureInfo.InvariantCulture) <= maximum;
    }
}
