using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Serilog.Core;

using Homespool.Data;
using Homespool.Host.Certificates;
using Homespool.Host.Controllers;
using Homespool.Host.Listeners;
using Homespool.Host.PrusaConnect;

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

        // The settings file is the one path resolved before the container exists, so it cannot go
        // through IHostEnvironmentAccessor like every other configurable directory - Program reads
        // builder.Environment.ContentRootPath, which under WebApplicationFactory is the real
        // Homespool.Host project folder. Left alone, one test that saves a setting writes
        // Homespool.Host/data/settings.json and every other host in the run then reads it: measured,
        // 15 end-to-end tests failed that way, and a developer's own server would have picked it up
        // too. This is the narrow hole the content-root remarks above describe, and this closes it.
        ConfigurationOverrides["Settings:File"] = Path.Combine(_contentRoot, "data", "settings.json");

        // Mandatory since the CA key became encrypted-only - a host without one refuses to start,
        // which is the production behaviour and not what a test host should die of. Set here rather
        // than relied on from appsettings.Development.json so it holds whatever environment a test
        // chooses.
        ConfigurationOverrides["Certificates:AuthorityPassphrase"] = "e2e test passphrase";
    }

    /// <summary>
    /// Configuration values layered over the application's own, for settings that must be in place
    /// <em>before</em> <c>Program</c> runs rather than overridden afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not the same mechanism as the service overrides below, and it cannot be.</b> Those replace a
    /// registration after the fact, which works for anything resolved from the container. Whether an
    /// authentication scheme is registered at all is decided while <c>Program</c> is still building the
    /// pipeline — <c>OidcOptions.IsConfigured</c> is read there — so a post-hoc
    /// <c>PostConfigure</c> arrives far too late to put a provider into
    /// <c>GetExternalAuthenticationSchemesAsync</c>.
    /// </para>
    /// <para>
    /// Added as the last source, so it wins over <c>appsettings.json</c>. That is the ordering
    /// <c>ConfigureAppConfiguration</c> does not guarantee for a callback registered on the builder —
    /// the trap recorded above, which cost a cycle when the connection string was done that way — but
    /// it does hold for <c>ConfigureHostConfiguration</c>-style layering applied here, and the tests
    /// assert the value that arrives rather than trusting it.
    /// </para>
    /// </remarks>
    public Dictionary<string, string?> ConfigurationOverrides { get; } = [];

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
    /// <para>
    /// <b>Production mints this on its startup path</b> - Program.EnsurePrinterCertificate, before the
    /// first request, because the proxy reads the leaf when it starts. A test host without a
    /// certificate was unlike production in ways that kept showing up: the provisioning bundle had no
    /// address to offer, and the certificate health check called a freshly started host degraded.
    /// </para>
    /// <para>
    /// <b>Corrected 2026-09-01 - the startup path is not skipped here, and this text used to say it
    /// was.</b> <c>WebApplicationFactory</c> intercepts at <c>app.Run()</c>, which is why Program has
    /// its <c>catch (HostAbortedException)</c>, so everything before that line runs in a test host
    /// too - the mint included. The <see cref="PrinterCertificateAuthority.EnsureLeaf"/> call below
    /// therefore always finds a leaf and short-circuits; it is kept because it costs a header read
    /// and states what this host requires rather than inheriting it by luck.
    /// </para>
    /// <para>
    /// <b>Which is why the certificates are planted before the host is built</b>, via
    /// <see cref="SharedPrinterCertificates"/>: a mint costs about 1.2 seconds of key derivation, this
    /// runs per test, and it was 39% of the suite. Planting after <c>base.CreateHost</c> returns is
    /// too late to save any of it - the host has already minted - and looks like it works, because the
    /// files are then correct either way.
    /// </para>
    /// </remarks>
    [SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits",
                     Justification =
                         "CreateHost is a synchronous override of the test host factory; there is no asynchronous form to call.")]
    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Before the host is built, not after: Program mints on its own startup path, so a copy
        // planted afterwards would be correct and worthless. The content root is created here for the
        // same reason - ConfigureServices does it too, but that runs inside the call below.
        Directory.CreateDirectory(_contentRoot);

        SharedPrinterCertificates.Plant(_contentRoot);

        IHost host = base.CreateHost(builder);

        PrusaConnectOptions connect = host.Services.GetRequiredService<IOptions<PrusaConnectOptions>>().Value;

        if (connect.PrinterTls)
        {
            PrinterCertificateAuthority authority =
                host.Services.GetRequiredService<PrinterCertificateAuthority>();

            authority.EnsureLeaf(PrinterCertificateNames.ForThisMachineAsync(
                                     connect,
                                     host.Services.GetRequiredService<IOptions<CertificateOptions>>()
                                         .Value.ParsedContainerNetworks,
                                     host.Services.GetRequiredService<IHostAddressResolver>(),
                                     CancellationToken.None).GetAwaiter().GetResult())
                     .Dispose();

            SharedPrinterCertificates.Capture(authority, _contentRoot);
        }

        return host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (ConfigurationOverrides.Count > 0)
        {
            builder.UseConfiguration(new ConfigurationBuilder()
                                     .AddInMemoryCollection(ConfigurationOverrides)
                                     .Build());
        }

        builder.ConfigureServices(services =>
        {
            RegisteredServices = [.. services];

            // Gives TestServer the one thing it has no way to have: a listener. Real requests carry
            // the port they arrived on in Connection.LocalPort, which is what the segregation
            // middleware reads and what a client cannot forge; TestServer accepts no connections at
            // all, so that port is 0 for every request and every printer route would be refused.
            //
            // So the test's choice of port - the one in its base address - stands in for the choice
            // of listener, and a test reaches the printer listener by connecting to its port. That keeps the
            // production path free of test seams: nothing in Homespool.Host consults the Host header,
            // here or anywhere.
            services.AddSingleton<IStartupFilter>(provider =>
                                                      new SimulatedListener(
                                                          provider.GetRequiredService<IOptions<ListenerOptions>>()));

            ServiceDescriptor? descriptor =
                services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<HomespoolDbContext>));

            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<HomespoolDbContext>(options => options.UseSqlite(_connectionString));

            // Everything that keeps a file resolves its configured, relative directory against this.
            // Replacing it is what isolates uploads, certificates and whatever comes next, in one
            // place, instead of overriding each component's options as it is discovered escaping -
            // which is how the first three were dealt with, one incident at a time.
            Directory.CreateDirectory(_contentRoot);

            ServiceDescriptor? environment = services.SingleOrDefault(d => d.ServiceType == typeof(IHostEnvironmentAccessor));

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
                ServiceDescriptor? dispatcherDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(MessageDispatcher));

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
