using System;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.Services;

/// <summary>
/// Restarts the service on an administrator's request, by stopping it and leaving the start to
/// whatever supervises the process.
/// </summary>
/// <remarks>
/// <para>
/// <b>A restart is a clean stop plus somebody else's start.</b> Stopping goes through the ordinary
/// shutdown - the telemetry flush, the saved live state - exactly as a <c>docker compose restart</c>
/// would, and nothing here needs any power over the host. What brings the process back is the
/// container's <c>restart: unless-stopped</c>; without it this is an off switch.
/// </para>
/// <para>
/// <b>Offered only inside a container</b>, which is what the official .NET images declare with
/// <c>DOTNET_RUNNING_IN_CONTAINER</c>. Under <c>dotnet run</c> nothing restarts the process, and a
/// button that only stops the server would say something untrue.
/// </para>
/// </remarks>
public sealed class ApplicationRestarter
{
    /// <summary>
    /// Set to <c>true</c> by the .NET container images, and read through configuration so a test host
    /// can claim it.
    /// </summary>
    public const string ContainerVariable = "DOTNET_RUNNING_IN_CONTAINER";

    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ApplicationRestarter> _logger;

    public ApplicationRestarter(IConfiguration configuration,
                                IHostApplicationLifetime lifetime,
                                ILogger<ApplicationRestarter> logger)
    {
        _configuration = configuration;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>Whether something will start the service again after it stops.</summary>
    public bool IsAvailable =>
        string.Equals(_configuration[ContainerVariable], "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Stops the service so its supervisor starts it again.</summary>
    /// <param name="administratorId">Who asked, for the log.</param>
    /// <exception cref="InvalidOperationException">Nothing would start the service again.</exception>
    public void Restart(long administratorId)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("Nothing supervises this process, so stopping it would not restart it.");
        }

        _logger.LogWarning("Administrator {AdministratorId} restarted the service.", administratorId);

        _lifetime.StopApplication();
    }
}
