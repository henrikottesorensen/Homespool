using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Homespool.Host.Configuration;

/// <summary>
/// Where the settings file is, and that it is read by <see cref="SettingsFileProvider"/>.
/// </summary>
public sealed class SettingsFileSource : JsonConfigurationSource
{
    /// <inheritdoc />
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);

        return new SettingsFileProvider(this);
    }
}
