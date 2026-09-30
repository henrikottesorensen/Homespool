using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
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
/// a free port - and it has to be known before the host starts, because the listener options refuse
/// two listeners on one port and a request's listener is told apart by the port it arrived on.
/// </para>
/// <para>
/// <b>Not a port the kernel chose.</b> Asking for port zero and releasing what comes back leaves the
/// port free until the host binds it, seconds later under load, and this suite asks for port zero
/// about five times per host started - three probes and the fake sidecar's two listeners, with
/// several classes starting hosts at once. The kernel hands a freed port straight back out as readily
/// as any other, so a host would sometimes find its port already taken by another test's host or
/// sidecar. The ports here come from below every kernel's ephemeral range, where nothing asking for
/// port zero is ever given one, and from one counter, so no two hosts in this process are given the
/// same one.
/// </para>
/// <para>
/// <b>Addressed as <c>localhost</c>, not <c>127.0.0.1</c></b>: the host's allowed-hosts list names
/// the one and not the other, and a browser sends the address it was given as its Host header.
/// </para>
/// </remarks>
public sealed class CameraHost : IAsyncDisposable
{
    /// <summary>
    /// The first port <see cref="FreePort"/> hands out: below Linux's ephemeral range, which starts at
    /// 32768, and far below macOS's and Windows', which start at 49152.
    /// </summary>
    private const int FirstPort = 20000;

    private const int PortCount = 12000;

    private static int _lastPort = RandomNumberGenerator.GetInt32(PortCount);

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

    /// <summary>
    /// An account with a printer on its team and nothing else, for a page that needs only that. The
    /// client is signed in as the account and already points at this host's listener.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                     Justification = "The client is the caller's to dispose; the catch disposes it when there is no caller to hand it to.")]
    public async Task<(HttpClient client, Guid printer)> AccountWithPrinterAsync(string email)
    {
        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(Factory, email);

        try
        {
            client.BaseAddress = BaseAddress;

            return (client, await SeedPrinterAsync(user.Id));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Signs in through the real login page and opens the printer's page.</summary>
    public static async Task<IPage> OpenPrinterPageAsync(IBrowserContext context, string email, Guid printer)
    {
        IPage page = await SignInAsync(context, email);

        await page.GotoAsync($"/Printers/Detail/{printer}");

        return page;
    }

    /// <summary>Signs in through the real login page, leaving the page wherever that lands.</summary>
    public static async Task<IPage> SignInAsync(IBrowserContext context, string email)
    {
        ArgumentNullException.ThrowIfNull(context);

        IPage page = await context.NewPageAsync();

        await page.GotoAsync("/Account/Login");
        await page.FillAsync("#Input_Login", email);
        await page.FillAsync("#Input_Password", EnrolmentFlowHelper.AccountPassword);
        await page.ClickAsync("#login-submit");
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));

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

    /// <summary>
    /// A port for one of the host's listeners: below the range a bind on port zero is given, never
    /// handed out twice by this process, and free at the moment it is handed out.
    /// </summary>
    /// <remarks>
    /// Checked by binding it exactly as Kestrel will - dual-mode on <c>[::]</c> - so a port anything
    /// holds on any address is passed over, where a loopback probe would miss one held on another
    /// interface. The counter starts at a random point so that two suites running side by side on one
    /// machine walk different ports; that they could still meet is the one race left, and it needs a
    /// second process picking fixed ports in the same range at the same moment.
    /// </remarks>
    private static int FreePort()
    {
        for (int attempt = 0; attempt < PortCount; attempt++)
        {
            int port = FirstPort + (int)((uint)Interlocked.Increment(ref _lastPort) % PortCount);

            if (IsFree(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException($"No port between {FirstPort} and {FirstPort + PortCount - 1} is free.");
    }

    private static bool IsFree(int port)
    {
        bool dualMode = Socket.OSSupportsIPv6;

        using Socket probe = new(dualMode ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork,
                                 SocketType.Stream,
                                 ProtocolType.Tcp);

        if (dualMode)
        {
            probe.DualMode = true;
        }

        try
        {
            probe.Bind(new IPEndPoint(dualMode ? IPAddress.IPv6Any : IPAddress.Any, port));

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
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
