using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.E2ETest;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.BrowserTest;

/// <summary>
/// A Homespool host on a real listener, a fake camera sidecar behind it, and one signed-in account
/// with a printer - everything a browser needs to open a printer page with a camera on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every listener on a port of its own.</b> The application binds the user, printer and transfer
/// listeners itself, on fixed defaults a developer's own server would also be using, so each is given
/// a free port. The port is found by binding and releasing it, which leaves a moment in which
/// something else could take it; nothing on a test machine is racing for ephemeral ports at that
/// rate.
/// </para>
/// <para>
/// <b>Addressed as <c>localhost</c>, not <c>127.0.0.1</c></b>: the host's allowed-hosts list names
/// the one and not the other, and a browser sends the address it was given as its Host header.
/// </para>
/// </remarks>
public sealed class CameraHost : IAsyncDisposable
{
    private readonly ScratchDirectory _scratch;

    private CameraHost(ScratchDirectory scratch, FakeGo2Rtc sidecar, HomespoolFactory factory, Uri baseAddress)
    {
        _scratch = scratch;
        Sidecar = sidecar;
        Factory = factory;
        BaseAddress = baseAddress;
    }

    public FakeGo2Rtc Sidecar { get; }

    public HomespoolFactory Factory { get; }

    /// <summary>The user listener, as a browser addresses it.</summary>
    public Uri BaseAddress { get; }

    /// <summary>
    /// Starts a host against a fresh fake sidecar. <paramref name="configure"/> sees the factory
    /// before it starts, for configuration a test needs in place from the first request.
    /// </summary>
    public static async Task<CameraHost> StartAsync(string name, Action<HomespoolFactory>? configure = null)
    {
        RequireClientLibraries();

        ScratchDirectory scratch = ScratchDirectory.Create(name);
        FakeGo2Rtc sidecar = await FakeGo2Rtc.StartAsync();
        HomespoolFactory factory = new(scratch);

        sidecar.ApplyTo(factory);

        int userPort = FreePort();
        factory.ConfigurationOverrides["Listeners:UserPort"] = userPort.ToString(CultureInfo.InvariantCulture);
        factory.ConfigurationOverrides["Listeners:PrinterPort"] = FreePort().ToString(CultureInfo.InvariantCulture);
        factory.ConfigurationOverrides["Listeners:TransferPort"] = FreePort().ToString(CultureInfo.InvariantCulture);

        configure?.Invoke(factory);

        factory.UseKestrel();
        factory.StartServer();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();
        }

        return new CameraHost(scratch, sidecar, factory, new Uri($"http://localhost:{userPort}/"));
    }

    /// <summary>
    /// An account with a printer on its team and a camera bound to that printer, added through the
    /// Cameras page as a person would - so the sidecar holds its stream and its codecs are known.
    /// </summary>
    /// <returns>The account's sign-in email, and the printer whose page shows the camera.</returns>
    public async Task<(string email, Guid printer)> AccountWithCameraAsync(string email, string source)
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(Factory, email);

        using (client)
        {
            // A client the factory makes points at its default address rather than the listener this
            // host bound, so it is pointed there before its first request.
            client.BaseAddress = BaseAddress;

            Guid printer = await SeedPrinterAsync(user.Id);
            Camera camera = await CameraPage.AddNetworkCameraAsync(Factory, client, user, "Bed camera", source, printer);
            camera.PrinterId.Should().NotBeNull("the camera has to be on the printer's page to be watched there");

            return (email, printer);
        }
    }

    /// <summary>Signs in through the real login page and opens the printer's page.</summary>
    public static async Task<IPage> OpenPrinterPageAsync(IBrowserContext context, string email, Guid printer)
    {
        ArgumentNullException.ThrowIfNull(context);

        IPage page = await context.NewPageAsync();

        await page.GotoAsync("/Account/Login");
        await page.FillAsync("#Input_Login", email);
        await page.FillAsync("#Input_Password", EnrolmentFlowHelper.AccountPassword);
        await page.ClickAsync("#login-submit");
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));

        await page.GotoAsync($"/Printers/Detail/{printer}");

        return page;
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Sidecar.DisposeAsync();

        _scratch.Dispose();
    }

    /// <summary>
    /// Skips the test when Bootstrap has not been restored. Its display utilities are what hide and
    /// show every part of a camera panel, so without them nothing a browser test asserts about what is
    /// on screen means anything - an element carrying <c>d-none</c> is simply visible.
    /// </summary>
    private static void RequireClientLibraries([CallerFilePath] string thisFile = "")
    {
        string bootstrap = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!,
                                        "Homespool.Host", "wwwroot", "lib", "bootstrap");

        if (!Directory.Exists(bootstrap))
        {
            Assert.Skip("The client libraries are not restored, so no page here has its styles. Run " +
                        "`dotnet libman restore` in Homespool.Host.");
        }
    }

    private static int FreePort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();

        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>A printer on the account's default team, inserted directly - enrolment is not the subject.</summary>
    private async Task<Guid> SeedPrinterAsync(long userId)
    {
        using IServiceScope scope = Factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await context.TeamMembers
                                             .SingleAsync(member => member.UserId == userId && member.IsDefault,
                                                          TestContext.Current.CancellationToken);

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            Name = "Bench printer",
            Type = PrinterType.PrusaConnect,
            TeamId = membership.TeamId,
            Status = PrinterStatus.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return printer.Uuid;
    }
}
