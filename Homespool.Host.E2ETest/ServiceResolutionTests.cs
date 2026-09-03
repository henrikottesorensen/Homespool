using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Every service this project registers can actually be constructed from the real container.
/// </summary>
/// <remarks>
/// <para>
/// <b>A registration is not exercised until something resolves it</b>, so an unsatisfiable one is
/// invisible to a clean build and to a green unit suite - unit tests construct classes directly - and
/// even to a started host, if nothing has asked for the service yet. That window is exactly when a
/// constructor signature is still moving.
/// </para>
/// <para>
/// <b>Hand-rolled rather than using <c>ValidateOnBuild</c></b>, which was tried first: set through
/// <c>builder.Host.UseDefaultServiceProvider</c> it does <i>not</i> take effect on a
/// <c>WebApplicationBuilder</c> - an application with a deliberately unsatisfiable singleton started
/// happily with the flag on. Relying on it would have been a guard that never ran, which this
/// repository has done four times already.
/// </para>
/// <para>
/// <b>No known defect motivated this.</b> One was suspected - <c>PrinterCertificateAuthority</c>
/// injects <c>TimeProvider</c>, which no other class here takes from the container - but it proved
/// resolvable anyway, from somewhere in the Identity/EF/hosting graph. The suspicion came from
/// probing a bare <c>WebApplication.CreateBuilder()</c>, which does not provide it; generalising from
/// that to the real application was the mistake. The guard is worth having regardless, for the next
/// registration whose dependencies are not yet in the graph.
/// </para>
/// </remarks>
public sealed class ServiceResolutionTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-resolve-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");
        _ = _factory.Server;

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Every service Homespool itself registers resolves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scoped to types declared in this project's own assemblies. Framework registrations are excluded
    /// deliberately: there are hundreds, we cannot fix them, and some legitimately need an active
    /// request to construct - so including them would trade a useful signal for noise, and noise is
    /// how a real failure hides.
    /// </para>
    /// <para>
    /// Open generics are skipped because there is no closed type to ask for, and hosted services are
    /// skipped because the host has already constructed them by the time this runs - if one of those
    /// could not be built, the factory would have thrown in <c>InitializeAsync</c>.
    /// </para>
    /// <para>
    /// Every failure is collected rather than thrown on the first, so one run names all of them.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryServiceThisProjectRegistersCanBeConstructed()
    {
        // Arrange
        Type[] ours =
        [
            typeof(Homespool.Host.Program),
            typeof(Homespool.Data.HomespoolDbContext),
            typeof(Homespool.Model.Entities.HSUser),
        ];

        HashSet<System.Reflection.Assembly> ownAssemblies = [.. ours.Select(t => t.Assembly)];

        List<ServiceDescriptor> candidates =
        [
            .. _factory.RegisteredServices
                       .Where(d => ownAssemblies.Contains(d.ServiceType.Assembly))
                       .Where(d => !d.ServiceType.ContainsGenericParameters)
                       .Where(d => d.ServiceType != typeof(Microsoft.Extensions.Hosting.IHostedService)),
        ];

        candidates.Should().NotBeEmpty("if this filters everything out, the test proves nothing");

        // Act
        List<string> failures = [];

        using IServiceScope scope = _factory.Services.CreateScope();

        foreach (ServiceDescriptor descriptor in candidates.DistinctBy(d => d.ServiceType))
        {
            try
            {
                scope.ServiceProvider.GetRequiredService(descriptor.ServiceType);
            }
            catch (Exception ex)
            {
                failures.Add($"{descriptor.ServiceType.Name}: {ex.Message.Split('\n')[0]}");
            }
        }

        // Assert
        failures.Should().BeEmpty(
            "a registration nothing resolves yet is untested by construction, and this is the only "
            + "thing that exercises it");
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _factory?.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
