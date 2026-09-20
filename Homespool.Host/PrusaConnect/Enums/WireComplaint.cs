namespace Homespool.Host.PrusaConnect.Enums;

/// <summary>What <see cref="PrinterWireComplaints"/> can say about something a printer sent.</summary>
public enum WireComplaint
{
    Undefined = 0,

    /// <summary>A whole message was refused: not JSON, or JSON that is not a printer message.</summary>
    UnreadableMessage = 1,

    /// <summary>An <c>INFO</c> event was kept but its data could not be read, so no identity came of it.</summary>
    UnreadableInfo = 2,

    /// <summary>The message carried <c>nan</c> or <c>inf</c>, and was read with those replaced.</summary>
    NonFiniteNumbers = 3,

    /// <summary>A body posted over HTTP was past the size ceiling, and refused unread.</summary>
    BodyTooLarge = 4,

    /// <summary>An inline transfer chunk was asked for over HTTP, where there is no socket to answer on.</summary>
    InlineTransferOverHttp = 5,
}
