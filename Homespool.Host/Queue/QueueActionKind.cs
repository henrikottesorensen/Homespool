namespace Homespool.Host.Queue;

public enum QueueActionKind
{
    Undefined = 0,

    /// <summary>No queue, or no connection. Not a stall - there is genuinely nothing to do.</summary>
    Nothing,

    /// <summary>Send the head's file to the printer.</summary>
    Transfer,

    /// <summary>Start printing the head.</summary>
    Print,

    /// <summary>Something is in the way, and it will clear on its own or with a person's help.</summary>
    Wait,
}
