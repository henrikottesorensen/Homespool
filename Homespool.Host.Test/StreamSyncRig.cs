using System;
using System.Net.Http;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Homespool.Data;
using Homespool.Host.Cameras;

namespace Homespool.Host.Test;

/// <summary>
/// A <see cref="CameraStreamSync"/> wired as the application wires it, over one SQLite file and a
/// <see cref="SidecarHandler"/>: every scope gets a context of its own on that file, so a row a test
/// changes is what the sync reads next.
/// </summary>
/// <remarks>
/// The device list is empty, because <c>LocalCameraDevices</c> reads a directory that exists only in
/// the container. So every attached source is one naming a device this machine does not have, which
/// is the same verdict a forged name earns on a real machine.
/// </remarks>
internal sealed class StreamSyncRig : IDisposable
{
    private readonly ServiceProvider _services;

    public StreamSyncRig(string databasePath, HttpMessageHandler sidecar, CameraSourcePolicy? policy = null)
    {
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(sidecar, disposeHandler: false));

        Client = new Go2RtcClient(factory,
                                  TestOptions.Monitor(new CameraOptions
                                  {
                                      ApiUsername = "homespool",
                                      ApiPassword = "secret", // betterleaks:allow - the sidecar is a handler in this test
                                  }),
                                  NullLogger<Go2RtcClient>.Instance);

        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={databasePath}")
                                                       .Options;

        ServiceCollection services = [];
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped(_ => new HomespoolDbContext(options));
        services.AddSingleton(Client);
        services.AddSingleton(new CameraCredentialProtector(new EphemeralDataProtectionProvider(),
                                                            NullLogger<CameraCredentialProtector>.Instance));
        services.AddSingleton(new LocalCameraDevices(NullLogger<LocalCameraDevices>.Instance,
                                                     new UsbDeviceNames(NullLogger<UsbDeviceNames>.Instance),
                                                     TestOptions.Monitor(new CameraOptions())));
        services.AddSingleton(policy ?? CameraSourcePolicyTests.Build());
        services.AddSingleton<CameraStreamSync>();
        services.AddScoped<CameraStreamSweeper>();

        _services = services.BuildServiceProvider();
    }

    public Go2RtcClient Client { get; }

    public CameraStreamSync Sync => _services.GetRequiredService<CameraStreamSync>();

    public IServiceScopeFactory Scopes => _services.GetRequiredService<IServiceScopeFactory>();

    public void Dispose()
    {
        _services.Dispose();
    }
}
