using System.Collections.Generic;
using System.Linq;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using PrinterService.Data;
using PrinterService.Host.Controllers;

using Serilog.Core;

namespace PrinterService.Host.Test;

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> pointed at an isolated temp-file SQLite
/// database, shared by every test that drives the real ASP.NET Core pipeline instead of calling
/// services directly (<see cref="EndToEndEnrollmentTests"/> and friends).
/// </summary>
/// <remarks>
/// <para>
/// <c>Program</c> can't be the type argument - it's a <c>static class</c>, and static types can't be
/// generic arguments (CS0718). <see cref="WebApplicationFactory{TEntryPoint}"/> only needs
/// <c>TEntryPoint</c> to be <em>some</em> public type in the entry-point assembly - it locates
/// <c>Assembly.EntryPoint</c> via reflection from there rather than calling anything on the type
/// itself - so <see cref="PrinterAppController"/> works and doubles as a pointer to what these tests
/// exercise. Confirmed compatible with <c>Program.cs</c>'s minimal-hosting <c>Main</c> by the
/// pre-existing <c>catch (HostAbortedException)</c> block there, added for <c>dotnet-ef</c>'s
/// design-time tooling: both rely on the same <c>HostFactoryResolver</c> interception mechanism.
/// </para>
/// <para>
/// The connection-string override went through two attempts. The first,
/// <c>ConfigureWebHost(b =&gt; b.ConfigureAppConfiguration(...))</c> adding an in-memory override,
/// silently lost to <c>appsettings.json</c>'s <c>ConnectionStrings:PrinterServiceDb</c> -
/// <c>WebApplicationFactory</c>'s minimal-hosting interception doesn't guarantee a
/// <c>ConfigureAppConfiguration</c> callback registered this way runs after <c>Program</c>'s own
/// configuration sources. Every test run was therefore hitting the one real
/// <c>PrinterService.Sqlite</c> file in the test output directory instead of an isolated temp file,
/// accumulating state across runs. Fixed by removing and re-adding the
/// <see cref="DbContextOptions{TContext}"/> service descriptor directly in <c>ConfigureServices</c>,
/// sidestepping configuration precedence entirely - the pattern Microsoft's own integration-testing
/// docs use.
/// </para>
/// </remarks>
public sealed class PrinterServiceFactory : WebApplicationFactory<PrinterAppController>
{
    private readonly string _connectionString;
    private readonly IReadOnlyList<ILogEventSink> _extraSinks;

    public PrinterServiceFactory(string connectionString, params IReadOnlyList<ILogEventSink> extraSinks)
    {
        _connectionString = connectionString;
        _extraSinks = extraSinks;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            ServiceDescriptor? descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<PSDbContext>));

            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<PSDbContext>(options => options.UseSqlite(_connectionString));

            // Program.cs's .ReadFrom.Services(services) call wires up any ILogEventSink registered
            // here alongside its own console sink - a bare Microsoft.Extensions.Logging.ILoggerProvider
            // registered the same way does *not* work, because AddSerilog replaces ILoggerFactory with
            // a bridge to the one configured Serilog pipeline rather than fanning out to independently
            // registered logging providers. This is how a test observes log output (e.g.
            // AdminBootstrap's one-time setup token, never exposed any other way by design) without
            // touching Program.cs.
            foreach (ILogEventSink sink in _extraSinks)
            {
                services.AddSingleton(sink);
            }
        });
    }
}
