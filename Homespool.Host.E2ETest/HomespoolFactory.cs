using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Homespool.Data;
using Homespool.Host.Controllers;
using Homespool.Host.Listeners;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Transfers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog.Core;

namespace Homespool.Host.E2ETest;

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> pointed at an isolated temp-file SQLite
/// database, shared by every test that drives the real ASP.NET Core pipeline instead of calling
/// services directly (<see cref="EndToEndEnrolmentTests"/> and friends).
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
/// silently lost to <c>appsettings.json</c>'s <c>ConnectionStrings:HomespoolDb</c> -
/// <c>WebApplicationFactory</c>'s minimal-hosting interception doesn't guarantee a
/// <c>ConfigureAppConfiguration</c> callback registered this way runs after <c>Program</c>'s own
/// configuration sources. Every test run was therefore hitting the one real
/// <c>Homespool.Sqlite</c> file in the test output directory instead of an isolated temp file,
/// accumulating state across runs. Fixed by removing and re-adding the
/// <see cref="DbContextOptions{TContext}"/> service descriptor directly in <c>ConfigureServices</c>,
/// sidestepping configuration precedence entirely - the pattern Microsoft's own integration-testing
/// docs use.
/// </para>
/// </remarks>
public sealed class HomespoolFactory : WebApplicationFactory<PrinterAppController>
{
    /// <summary>The printer address every test host advertises, and the first name in its certificate.</summary>
    public const string PrinterHost = "printers.example.com";

    private readonly string _connectionString;
    private readonly IReadOnlyList<ILogEventSink> _extraSinks;
    private readonly MessageDispatcher? _messageDispatcher;

    /// <summary>
    /// The content root this factory's application resolves relative paths against, deleted with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One redirection, at the only seam that matters.</b> Components that keep files -
    /// <c>UploadedFileStore</c>, <c>PrinterCertificateAuthority</c> - hold a relative directory in
    /// options and resolve it through <see cref="IHostEnvironmentAccessor"/>. Under
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> that content root is the <i>real project
    /// directory</i>, not the test output folder, so those relative paths land in the same
    /// <c>Homespool.Host/data</c> a dev server uses. Pointing the accessor here moves all of them at
    /// once, including the ones nobody has written yet.
    /// </para>
    /// <para>
    /// <b>It has caught three components, one at a time, and that is the point of fixing the
    /// mechanism instead.</b> The SQLite file took two attempts. Uploads went unnoticed until 21 stale
    /// directories had accumulated in the project tree. Certificates were worse than untidy: a test
    /// issued one, read back the developer's own from a previous live run, and asserted against
    /// <i>that</i> - a passing test measuring the wrong machine. Each was fixed on its own; each fix
    /// left the next component to rediscover the trap.
    /// </para>
    /// <para>
    /// The narrow hole this leaves: a component that injects <c>IWebHostEnvironment</c> and does its
    /// own <c>Path.Combine</c> bypasses the accessor and this override with it. That is a convention
    /// held by <see cref="HostEnvironmentAccessor"/>'s own documentation rather than by code, and
    /// <c>ContentRootIsIsolatedTests</c> is what notices if this override is removed.
    /// </para>
    /// </remarks>
    private readonly string _contentRoot =
        Path.Combine(Path.GetTempPath(), $"hs-content-{Guid.NewGuid():N}");

    public HomespoolFactory(string connectionString,
                                 MessageDispatcher? messageDispatcher = null,
                                 params IReadOnlyList<ILogEventSink> extraSinks)
    {
        _connectionString = connectionString;
        _messageDispatcher = messageDispatcher;
        _extraSinks = extraSinks;
    }

    /// <summary>
    /// Every service descriptor the real application registered.
    /// </summary>
    /// <remarks>
    /// Captured so a test can try to construct each one. A registration is not exercised until
    /// something resolves it, so an unsatisfiable one is invisible to a clean build, a green unit
    /// suite and even a started host - see <c>ServiceResolutionTests</c>.
    /// </remarks>
    public IReadOnlyList<ServiceDescriptor> RegisteredServices { get; private set; } = [];

    /// <summary>
    /// Issues the printer certificate the way startup would, because nothing here binds a listener.
    /// </summary>
    /// <remarks>
    /// <b>Production mints this while configuring Kestrel</b> - the printer listener cannot bind
    /// without it - so a test host that skipped it was unlike production in a way that kept showing
    /// up: the provisioning bundle had no address to offer, and the certificate health check called a
    /// freshly started host degraded. Doing it here makes a test host resemble a started server, which
    /// is what these tests are for.
    /// </remarks>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        IHost host = base.CreateHost(builder);

        PrusaConnectOptions connect = host.Services.GetRequiredService<IOptions<PrusaConnectOptions>>().Value;

        if (connect.PrinterTls)
        {
            host.Services.GetRequiredService<Homespool.Host.Certificates.PrinterCertificateAuthority>()
                .EnsureLeaf(Homespool.Host.Certificates.PrinterCertificateNames.ForThisMachine(connect))
                .Dispose();
        }

        return host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            RegisteredServices = [.. services];

            // Gives TestServer the one thing it has no way to have: a listener. Real requests carry
            // the port they arrived on in Connection.LocalPort, which is what the segregation
            // middleware reads and what a client cannot forge; TestServer accepts no connections at
            // all, so that port is 0 for every request and every printer route would be refused.
            //
            // So the test's choice of port - the one in its base address - stands in for the choice
            // of listener, and a test dials the printer listener by dialling its port. That keeps the
            // production path free of test seams: nothing in Homespool.Host consults the Host header,
            // here or anywhere.
            services.AddSingleton<IStartupFilter>(
                provider => new SimulatedListener(provider.GetRequiredService<IOptions<ListenerOptions>>()));

            ServiceDescriptor? descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<HSDbContext>));

            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<HSDbContext>(options => options.UseSqlite(_connectionString));

            // Everything that keeps a file resolves its configured, relative directory against this.
            // Replacing it is what isolates uploads, certificates and whatever comes next, in one
            // place, instead of overriding each component's options as it is discovered escaping -
            // which is how the first three were dealt with, one incident at a time.
            Directory.CreateDirectory(_contentRoot);

            ServiceDescriptor? environment = services.SingleOrDefault(
                d => d.ServiceType == typeof(IHostEnvironmentAccessor));

            if (environment is not null)
            {
                services.Remove(environment);
            }

            services.AddSingleton<IHostEnvironmentAccessor>(new HostEnvironmentAccessor(_contentRoot));

            // A deterministic printer address, so no test depends on what a developer happens to have
            // in appsettings.Development.json - which is a real machine's LAN address, and changes.
            services.PostConfigure<PrusaConnectOptions>(options => options.PrinterHost = PrinterHost);

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

            // Lets a test substitute a spy (e.g. CapturingMessageDispatcher) for the real, scoped
            // MessageDispatcher, so it can assert on what actually reached the WebSocket handler chain
            // instead of scraping console output. Singleton rather than scoped: the same instance must
            // be the one both the request pipeline writes into and the test reads back afterward, and
            // a scoped registration would hand each request its own throwaway copy.
            if (_messageDispatcher is not null)
            {
                ServiceDescriptor? dispatcherDescriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(MessageDispatcher));

                if (dispatcherDescriptor is not null)
                {
                    services.Remove(dispatcherDescriptor);
                }

                services.AddSingleton(_messageDispatcher);
            }
        });
    }

    /// <summary>
    /// Sets <see cref="ConnectionInfo.LocalPort"/> from the port the test addressed, so
    /// <c>ListenerSegregationMiddleware</c> sees the listener the test meant.
    /// </summary>
    /// <remarks>
    /// A request with no port in its address is the user listener, which is what
    /// <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/> produces by default - so every
    /// existing test keeps meaning what it meant, and only the printer-facing ones say otherwise.
    /// </remarks>
    private sealed class SimulatedListener : IStartupFilter
    {
        private readonly int _userPort;

        public SimulatedListener(IOptions<ListenerOptions> listeners)
        {
            _userPort = listeners.Value.UserPort;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.LocalPort = context.Request.Host.Port ?? _userPort;

                    await nextMiddleware();
                });

                next(app);
            };
        }
    }

    /// <summary>
    /// Removes the content root, and everything the application wrote into it, along with the host.
    /// Best effort: a leaked temp directory is a nuisance, a failed test run because cleanup threw is
    /// worse.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        try
        {
            if (Directory.Exists(_contentRoot))
            {
                Directory.Delete(_contentRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
