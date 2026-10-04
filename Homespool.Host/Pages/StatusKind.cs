namespace Homespool.Host.Pages;

/// <summary>
/// What a status message reports, which decides how its alert is dressed.
/// </summary>
/// <remarks>
/// <b>Said by the code that wrote the message, never read back out of the text.</b> The text is
/// localised, so any test of its wording holds in one language at best and silently passes as good
/// news in the others.
/// </remarks>
public enum StatusKind
{
    /// <summary>
    /// Not a kind. Present so a message nobody classified is not silently a success; it is shown as
    /// a <see cref="Warning"/>.
    /// </summary>
    Undefined = 0,

    /// <summary>It went through.</summary>
    Success = 1,

    /// <summary>Nothing happened and nothing is wrong: what was asked for was already so.</summary>
    Info = 2,

    /// <summary>Refused: nothing was done, and nothing is broken.</summary>
    Warning = 3,

    /// <summary>It failed, and somebody may have to do something about it.</summary>
    Danger = 4,
}
