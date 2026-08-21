namespace Homespool.Host.Pages.Printers;

/// <summary>
/// What the queue is doing about one of its entries, for the column that says so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived on every read, never stored.</b> <c>QueuedPrint</c> is deliberately property-less -
/// <c>notes/print-queue.md</c>: the printer runs a producer loop and the queue is just a list it
/// pulls from, so "prepared, waiting for the printer" is the loop sitting in not-ready rather than a
/// column on the row. This names that for a reader and adds no state to the entity.
/// </para>
/// <para>
/// <b>Only the head is ever anything but <see cref="Waiting"/></b>, because the loop never looks past
/// it - the spooler behaviour the design chose over skipping.
/// </para>
/// </remarks>
public enum QueueEntryStatus
{
    /// <summary>The zero value every enum here reserves for "nobody wrote this".</summary>
    Undefined = 0,

    /// <summary>In the list, with the loop not yet able to act on it.</summary>
    Waiting = 1,

    /// <summary>Its bytes are moving to the printer now.</summary>
    Sending = 2,

    /// <summary>
    /// Something is in the way that the loop cannot clear by itself - a full drive, a file the
    /// printer disagrees with. The queue holds behind it rather than skipping past.
    /// </summary>
    Held = 3,
}
