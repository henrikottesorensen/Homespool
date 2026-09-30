using System;

namespace Homespool.Host.Configuration;

/// <summary>
/// The settings file exists but does not hold a JSON object that can be read, and going on without it
/// would put the configured defaults in place of everything an administrator saved.
/// </summary>
/// <remarks>
/// Thrown at startup, where the configuration layer refuses the file anyway, and on a save, where
/// carrying on would write the defaults over the file and lose what it held for good. Every message
/// names the file and what fixes it, because at startup the only audience is a log.
/// </remarks>
public class SettingsFileUnreadableException : Exception
{
    public SettingsFileUnreadableException()
        : base("The settings file exists but cannot be read.")
    {
    }

    public SettingsFileUnreadableException(string message)
        : base(message)
    {
    }

    public SettingsFileUnreadableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
