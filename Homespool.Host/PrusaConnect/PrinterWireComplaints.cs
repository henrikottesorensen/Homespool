using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Microsoft.Extensions.Logging;

using Homespool.Host.PrusaConnect.Enums;
using Homespool.Host.Services;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Says, in the application log, that a printer sent something that could not be used - once, and
/// then at most once per <see cref="Interval"/> for the same printer and the same complaint, with a
/// count of what happened in between.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every site that goes through this can be driven by whoever holds a printer's token</b>, for
/// as long as the sender likes: an unreadable <c>INFO</c> or a message carrying <c>nan</c> does not
/// even cost the connection. On the socket <see cref="MessageBudget"/> bounds the rate, at five a
/// second by default; over HTTP the rate limiter does, at three. A line per occurrence is therefore
/// a way to fill the disk. See <see cref="LogThrottle"/> for the numbers
/// that rule came from.
/// </para>
/// <para>
/// <b>Per printer, not one throttle for all of them</b>, so that a printer making noise cannot use up
/// the line that would have named a different one, and so that the printer id in a summary is the
/// printer the count belongs to. Ids arrive authenticated, so the table is bounded by how many
/// printers are enrolled. It lives here because the classes that notice these things are created per
/// request and have nowhere to keep a count.
/// </para>
/// <para>
/// <b>The exception's message, never the exception.</b> The throw site is always the parser, so a
/// stack trace says nothing the message does not - and these are the printer's faults, which a trace
/// dresses up as ours. The message is cleaned and cut, because a parser quotes the bytes it choked
/// on and those are the sender's to choose.
/// </para>
/// </remarks>
public sealed class PrinterWireComplaints
{
    /// <summary>Long enough for a parser's message, position included; an absurd one is cut.</summary>
    private const int MaxDetailLength = 200;

    private readonly ILogger<PrinterWireComplaints> _logger;
    private readonly ConcurrentDictionary<(int printerId, WireComplaint complaint), LogThrottle> _throttles = new();

    public PrinterWireComplaints(ILogger<PrinterWireComplaints> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Minimum spacing between lines for one printer and one complaint. The same ten seconds as every
    /// other throttled site; settable so a test can close a window without waiting for it.
    /// </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A message, or part of one, could not be read and was refused.</summary>
    /// <param name="printerId">The printer it came from.</param>
    /// <param name="complaint">What was refused.</param>
    /// <param name="cause">What the parser said. Only its message is logged.</param>
    public void Refused(int printerId, WireComplaint complaint, Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);

        Say(printerId, complaint, LogText.Clean(cause.Message, MaxDetailLength));
    }

    /// <summary>Something was refused for a reason that is ours to word, not a parser's.</summary>
    /// <param name="printerId">The printer it came from.</param>
    /// <param name="complaint">What was refused.</param>
    /// <param name="detail">The particulars, in our own words. Nothing off the wire belongs here
    /// uncleaned; the other overload is the one that cleans.</param>
    public void Refused(int printerId, WireComplaint complaint, string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        Say(printerId, complaint, detail);
    }

    /// <summary>
    /// A message carried non-finite numbers that are not JSON, and was read all the same - see
    /// <see cref="NonFiniteNumberPatcher"/>. Said because what is stored will show no reading where
    /// the printer sent one of these, and nothing else explains why.
    /// </summary>
    /// <param name="printerId">The printer it came from.</param>
    /// <param name="tokens">What was replaced. The spellings are a sign and ASCII letters - the
    /// patcher's grammar admits nothing else - so they are logged as they are.</param>
    public void Mended(int printerId, IReadOnlyList<NonFiniteToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        Say(printerId,
            WireComplaint.NonFiniteNumbers,
            $"{tokens.Count} in this message: {string.Join(' ', tokens.Select(token => token.Spelling).Distinct())}");
    }

    /// <summary>
    /// A socket spent its <see cref="MessageBudget"/>, and the read loop is waiting before the next
    /// message. Said because the one thing it looks like from outside is a printer whose state lags.
    /// </summary>
    /// <param name="printerId">The printer the socket belongs to.</param>
    /// <param name="wait">How long this message waits.</param>
    /// <param name="perSecond">The budget's sustained rate, to say which setting governs it.</param>
    /// <param name="burst">The budget's burst.</param>
    public void OverBudget(int printerId, TimeSpan wait, int perSecond, int burst)
    {
        Say(printerId,
            WireComplaint.OverMessageBudget,
            string.Create(
                CultureInfo.InvariantCulture,
                $"waiting {wait.TotalMilliseconds:0.###} ms before the next one; the budget is {perSecond} a second after a burst of {burst} (PrusaConnect:MessagesPerSecond, PrusaConnect:MessageBurst)"));
    }

    private void Say(int printerId, WireComplaint complaint, string detail)
    {
        LogThrottle throttle = _throttles.GetOrAdd((printerId, complaint), _ => new LogThrottle(Interval));

        if (throttle.Record() is not { } window)
        {
            return;
        }

        string what = complaint switch
        {
            WireComplaint.UnreadableMessage => "sent a message that could not be read",
            WireComplaint.UnreadableInfo => "sent an INFO event whose data could not be read",
            WireComplaint.NonFiniteNumbers => "sent non-finite numbers that are not JSON",
            WireComplaint.BodyTooLarge => "posted a body over the size ceiling",
            WireComplaint.InlineTransferOverHttp => "requested an inline transfer chunk over HTTP, which cannot be served",
            WireComplaint.OverMessageBudget => "sent messages faster than its budget, and is being read more slowly",
            _ => throw new ArgumentOutOfRangeException(nameof(complaint), complaint, null),
        };

        if (window.IsFirstOccurrence)
        {
            _logger.LogWarning("Printer {PrinterId} {Complaint}: {Detail}", printerId, what, detail);

            return;
        }

        _logger.LogWarning(
            "Printer {PrinterId} {Complaint} - {Count} time(s) in the last {ElapsedSeconds:F0}s, {Total} since startup. The latest: {Detail}",
            printerId, what, window.Count, window.Elapsed.TotalSeconds, window.Total, detail);
    }
}
