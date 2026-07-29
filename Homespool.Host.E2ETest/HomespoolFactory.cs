using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Homespool.Data;
using Homespool.Host.Controllers;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Transfers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly string _connectionString;
    private readonly IReadOnlyList<ILogEventSink> _extraSinks;
    private readonly MessageDispatcher? _messageDispatcher;

    /// <summary>
    /// Where uploads go for this factory's lifetime, deleted with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The database is not the only persistent state a test run touches.</b>
    /// <see cref="FileStorageOptions.Directory"/> defaults to the relative <c>data/files</c>, and
    /// <c>UploadedFileStore</c> resolves a relative path against the content root - which under
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> is the <i>real project directory</i>, not the
    /// test output folder. So every upload test wrote into the same <c>Homespool.Host/data/files</c>
    /// a running dev server serves from, and nothing removed them: 21 stale directories had
    /// accumulated by 2026-07-28 before anyone noticed.
    /// </para>
    /// <para>
    /// That is the same fault this class's remarks already describe for the SQLite file, which took
    /// two attempts to fix. The file store arrived later and did not inherit the lesson. Isolating it
    /// here rather than in the one suite that uploads today means the next suite to touch the store
    /// gets it for free - which is the whole reason the database override lives here too.
    /// </para>
    /// </remarks>
    private readonly string _fileStorageRoot =
        Path.Combine(Path.GetTempPath(), $"hs-files-{Guid.NewGuid():N}");

    public HomespoolFactory(string connectionString,
                                 MessageDispatcher? messageDispatcher = null,
                                 params IReadOnlyList<ILogEventSink> extraSinks)
    {
        _connectionString = connectionString;
        _messageDispatcher = messageDispatcher;
        _extraSinks = extraSinks;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            ServiceDescriptor? descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<HSDbContext>));

            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<HSDbContext>(options => options.UseSqlite(_connectionString));

            // PostConfigure rather than Configure: Program.cs binds this section from configuration,
            // and only a post-configure step is guaranteed to run after that binding. An absolute
            // path also bypasses the content-root resolution entirely, so it cannot be re-rooted
            // back onto the project directory.
            services.PostConfigure<FileStorageOptions>(options => options.Directory = _fileStorageRoot);

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
    /// Removes the upload directory along with the host. Best effort: a leaked temp directory is a
    /// nuisance, a failed test run because cleanup threw is worse.
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
            if (Directory.Exists(_fileStorageRoot))
            {
                Directory.Delete(_fileStorageRoot, recursive: true);
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
