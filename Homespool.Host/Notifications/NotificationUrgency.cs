namespace Homespool.Host.Notifications;

/// <summary>
/// How hard a delivery service should try to reach a sleeping device - RFC 8030's four levels, which a
/// battery-saving phone uses to decide whether to wake for a message or hold it.
/// </summary>
public enum NotificationUrgency
{
    /// <summary>Never set. The zero value every enum here reserves for "nobody wrote this".</summary>
    Undefined = 0,

    /// <summary>Worth delivering only when the device is on power and a network anyway.</summary>
    VeryLow = 1,

    /// <summary>Worth delivering when the device is awake anyway.</summary>
    Low = 2,

    /// <summary>The ordinary case.</summary>
    Normal = 3,

    /// <summary>Wakes the device: a printer waiting for a person now.</summary>
    High = 4,
}
