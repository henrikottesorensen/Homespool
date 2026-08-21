namespace Homespool.Host.Pages.Printers;

/// <summary>
/// What a heater is doing, read from where it is against where it was told to be.
/// </summary>
/// <remarks>
/// <b>Derived rather than reported.</b> Nothing on the wire says "heating" - the printer sends a
/// temperature and a setpoint, and the relationship between them is the interesting part. Two
/// numbers side by side make a reader do that subtraction; naming it is the whole point of putting
/// it on a status card.
/// </remarks>
public enum HeaterState
{
    /// <summary>The zero value every enum here reserves for "nobody wrote this".</summary>
    /// <remarks>
    /// <b>Not the same as <see cref="Unknown"/>, and that is the point of having both.</b> This one
    /// means nothing computed a state - a default-valued field, a record built without one - where
    /// <see cref="Unknown"/> is a real answer about a real printer that has reported no temperature.
    /// Without the sentinel, whichever member sat first would silently become what "nobody looked"
    /// means. <see cref="HeaterReading.For"/> never produces it. See
    /// <c>notes/housekeeping.md</c>, "Enums need a reserved zero".
    /// </remarks>
    Undefined = 0,

    /// <summary>The printer has not reported this heater at all.</summary>
    Unknown = 1,

    /// <summary>Off and cold - no setpoint, nothing left over from a print.</summary>
    Off = 2,

    /// <summary>Off, but still hot enough to be worth knowing about.</summary>
    Cooling = 3,

    /// <summary>Below its setpoint and climbing.</summary>
    Heating = 4,

    /// <summary>At its setpoint, within the tolerance below.</summary>
    AtTarget = 5,
}
