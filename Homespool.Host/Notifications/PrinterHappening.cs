using Homespool.Model;

namespace Homespool.Host.Notifications;

/// <summary>
/// Something that happened to a printer and may be worth telling somebody about - the currency
/// between what notices a happening and what decides who hears of it.
/// </summary>
/// <remarks>
/// It carries what is known at the moment of noticing and no more. Who should hear, whether they
/// want to, and in what words are all decided later, by <see cref="NotificationRouter"/>, from the
/// database as it is then.
/// </remarks>
/// <param name="PrinterId">The printer it happened to.</param>
public abstract record PrinterHappening(int PrinterId);

/// <summary>A printer stopped to wait for a person: a dialog, or a red error screen.</summary>
/// <param name="PrinterId">The printer.</param>
/// <param name="Status">Attention or Error, whichever it reported.</param>
/// <param name="Code">The dialog's code, when the printer sent one.</param>
/// <param name="Text">The dialog's words, when the printer sent them.</param>
public sealed record PrinterNeedsAttention(int PrinterId, PrinterStatus Status, int? Code, string? Text)
    : PrinterHappening(PrinterId);

/// <summary>A print row was closed as finished, stopped or failed.</summary>
/// <param name="PrinterId">The printer it ran on.</param>
/// <param name="PrintJobId">The row, read again when the notification is composed.</param>
public sealed record PrintEnded(int PrinterId, long PrintJobId) : PrinterHappening(PrinterId);

/// <summary>A printer's queue became held behind something a person has to sort out.</summary>
/// <param name="PrinterId">The printer.</param>
/// <param name="Reason">Why, as the hold was recorded.</param>
public sealed record QueueHeld(int PrinterId, PrintHoldReason Reason) : PrinterHappening(PrinterId);

/// <summary>A print will stop for a filament change or a pause within a few minutes.</summary>
/// <param name="PrinterId">The printer.</param>
/// <param name="SecondsLeft">How long the printer said it had, when it crossed the threshold.</param>
public sealed record FilamentChangeSoon(int PrinterId, int SecondsLeft) : PrinterHappening(PrinterId);

/// <summary>A printer has not been connected for a while and has a print open.</summary>
/// <param name="PrinterId">The printer.</param>
/// <param name="PrintJobId">The print it had running when it went quiet.</param>
public sealed record PrinterLost(int PrinterId, long PrintJobId) : PrinterHappening(PrinterId);
