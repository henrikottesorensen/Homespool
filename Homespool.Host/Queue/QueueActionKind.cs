namespace Homespool.Host.Queue;

public enum QueueActionKind
{
    Undefined = 0,

    /// <summary>No queue, or no connection. Not a stall - there is genuinely nothing to do.</summary>
    Nothing = 1,

    /// <summary>Send the head's file to the printer.</summary>
    Transfer = 2,

    /// <summary>Start printing the head.</summary>
    Print = 3,

    /// <summary>Something is in the way, and it will clear on its own or with a person's help.</summary>
    Wait = 4,
}
