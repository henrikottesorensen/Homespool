using System;
using System.Collections.Generic;
using System.Linq;

using Homespool.Host.Localisation;
using Homespool.Host.PrintFiles;
using Homespool.Host.Queue;

namespace Homespool.Host.DTO;

/// <summary>A print just added to a queue, and anything whoever added it should be told.</summary>
/// <remarks>
/// <b>The entry exists whatever the warnings say.</b> A file the printer cannot print is still queued:
/// a <c>Hold</c> stops the queue when it reaches the entry, and fitting the right nozzle lets it run
/// without anybody queueing it again. This is the one moment somebody is there to be told.
/// </remarks>
public class EnqueuedPrintReadDTO : QueuedPrintReadDTO
{
    /// <summary>How the file and the printer disagree, most serious first. Empty when nothing is known to.</summary>
    public required IReadOnlyList<PrintWarningReadDTO> Warnings { get; set; }

    /// <summary>The entry the queue made, with its warnings said in the request's language.</summary>
    /// <param name="outcome">What the queue made of the request.</param>
    /// <param name="say">Turns a warning into a sentence in the request's language.</param>
    public static EnqueuedPrintReadDTO FromOutcome(EnqueueOutcome outcome, Func<MessageKey, string> say)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(say);

        // Findings and Warnings are the same list twice, in the same order - one as facts, one as
        // sentences - so the severity is read off the fact beside the sentence it belongs to.
        return new()
        {
            PrintUuid = outcome.Queued.PrintUuid,
            FileName = outcome.File.Name,
            Size = outcome.File.Size,
            Position = outcome.Queued.Position,
            QueuedAt = outcome.Queued.QueuedAt,
            Warnings = [.. outcome.Findings.Zip(outcome.Warnings, (finding, warning) => new PrintWarningReadDTO
            {
                Severity = PrintFileCompatibility.SeverityOf(finding).ToString(),
                Message = say(warning),
            })],
        };
    }
}
