using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.FakePrinter;
using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// What one <see cref="MinimumScopeRoute"/> needs to exist before its request can get past every
/// capability check: an owner, and whichever of a printer, a file, a queue entry, a print, a camera
/// or a claim code the route acts on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is arranged as the owner, over a cookie session</b>, so the token under test
/// carries nothing it needs only for setup. The owner's membership is the default
/// <see cref="CapabilityPresets.Manager"/>, which holds every printer and camera capability - so the
/// token's scope is the only thing narrowing the caller, and a refusal can only be the scope's.
/// </para>
/// <para>
/// <b>A connected printer brings its own owner</b>: enrolling a fake claims it as a freshly created
/// account, so <see cref="ConnectPrinterAsync"/> has to be the first thing a route arranges.
/// </para>
/// </remarks>
public sealed class MinimumScopeWorld : IAsyncDisposable
{
    /// <summary>A file the queue and the printer will both accept.</summary>
    public const string FileName = "benchy.gcode";

    /// <summary>A camera source the fake sidecar answers for.</summary>
    public const string CameraSource = "rtsp://192.0.2.1/minimum-scope";

    /// <summary>A camera watched over MJPEG, which the stream route serves and refuses an H.264 one.</summary>
    public static readonly FakeCamera MjpegCamera = FakeCamera.Jpeg with { TablesInStream = false };

    private readonly HomespoolFactory _factory;
    private readonly FakeGo2Rtc _sidecar;
    private readonly List<IAsyncDisposable> _asyncDisposables = [];
    private readonly List<IDisposable> _disposables = [];

    private HSUser? _owner;
    private HttpClient? _ownerClient;
    private HSUser? _teammate;
    private HttpClient? _teammateClient;

    public MinimumScopeWorld(HomespoolFactory factory, FakeGo2Rtc sidecar)
    {
        _factory = factory;
        _sidecar = sidecar;
    }

    /// <summary>The account every token is minted for.</summary>
    public HSUser Owner => _owner ?? throw new InvalidOperationException("No owner yet - arrange one first.");

    /// <summary>The printer the route acts on.</summary>
    public Guid Printer { get; private set; }

    /// <summary>The queue entry the route acts on.</summary>
    public Guid QueueEntry { get; private set; }

    /// <summary>The history row the route acts on.</summary>
    public Guid Print { get; private set; }

    /// <summary>The camera the route acts on.</summary>
    public Guid Camera { get; private set; }

    /// <summary>The live view the route acts on.</summary>
    public Guid View { get; private set; }

    /// <summary>A printer's registration code, waiting to be claimed.</summary>
    public string ClaimCode { get; private set; } = string.Empty;

    /// <summary>Creates the owner, unless a connected printer already brought one.</summary>
    public async Task EnsureOwnerAsync()
    {
        if (_owner is not null)
        {
            return;
        }

        (HSUser user, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, $"owner-{Guid.NewGuid():N}@example.com");

        UseOwner(user, client);
    }

    /// <summary>A printer on the owner's default team, inserted directly - for routes that never reach the printer.</summary>
    public async Task AddPrinterAsync()
    {
        await EnsureOwnerAsync();

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        TeamMember membership = await context.TeamMembers
                                             .SingleAsync(member => member.UserId == Owner.Id && member.IsDefault,
                                                          TestContext.Current.CancellationToken);

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = membership.TeamId,
            Status = PrinterStatus.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Printer = printer.Uuid;
    }

    /// <summary>An enrolled fake printer, connected and running, claimed by a new account that becomes the owner.</summary>
    /// <param name="device">Puts the device in the state the route needs, before it connects.</param>
    /// <param name="printerType">The model it reports; an MK3.5 when null.</param>
    public async Task ConnectPrinterAsync(Action<FakeDevice>? device = null, string? printerType = null)
    {
        if (_owner is not null)
        {
            throw new InvalidOperationException("A connected printer brings its own owner, so it is arranged first.");
        }

        PrinterIdentity random = PrinterIdentity.CreateRandom();
        PrinterIdentity? model = printerType is null ?
            null :
            new PrinterIdentity { Fingerprint = random.Fingerprint, SerialNumber = random.SerialNumber, PrinterType = printerType };

        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory, model);

        FakePrinterClient fake = new(identity, TimeProvider.System) { Token = token };
        device?.Invoke(fake.Device);

        await fake.ConnectAsync(FakePrinterConnections.ViaTestServerAsync(_factory), TestContext.Current.CancellationToken);
        Task run = fake.RunAsync(TestContext.Current.CancellationToken);

        _asyncDisposables.Add(new ConnectedFake(fake, run));

        await FakePrinterConnections.WaitUntilConnectedAsync(_factory, printerId);

        HSUser owner = await EnrolmentFlowHelper.FindUserAsync(_factory, userId);
        UseOwner(owner, await EnrolmentFlowHelper.SignInAsAsync(_factory, owner));

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        Printer = await context.Printers
                               .Where(printer => printer.Id == printerId)
                               .Select(printer => printer.Uuid)
                               .SingleAsync(TestContext.Current.CancellationToken);

        if (printerType is not null)
        {
            (await FakePrinterConnections.WaitUntilAsync(() => ModelOf(printerId) == printerType, TimeSpan.FromSeconds(10)))
                .Should().BeTrue("the printer's INFO, sent on connecting, names its model");
        }
    }

    /// <summary>Uploads <see cref="FileName"/> as the owner.</summary>
    public async Task UploadAsync()
    {
        await EnsureOwnerAsync();

        using StreamContent body = new(new MemoryStream(Encoding.UTF8.GetBytes("G28 ; home\n")));

        using HttpResponseMessage response = await _ownerClient!.PutAsync($"/api/v1/files/{FileName}", body,
                                                                          TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the upload is setup, not what the route verifies");
    }

    /// <summary>Queues <see cref="FileName"/> on <see cref="Printer"/> as the owner, uploading it first.</summary>
    public async Task EnqueueAsync()
    {
        await UploadAsync();

        QueueEntry = await EnqueueAsAsync(_ownerClient!);
    }

    /// <summary>
    /// Queues <see cref="FileName"/> on <see cref="Printer"/> as another member of the owner's team,
    /// so the entry is somebody else's work.
    /// </summary>
    public async Task EnqueueAsTeammateAsync()
    {
        HttpClient teammate = await TeammateAsync();

        using StreamContent body = new(new MemoryStream(Encoding.UTF8.GetBytes("G28 ; home\n")));

        using (HttpResponseMessage uploaded = await teammate.PutAsync($"/api/v1/files/{FileName}", body,
                                                                      TestContext.Current.CancellationToken))
        {
            uploaded.StatusCode.Should().Be(HttpStatusCode.OK, "the upload is setup, not what the route verifies");
        }

        QueueEntry = await EnqueueAsAsync(teammate);
    }

    /// <summary>A finished print of <see cref="FileName"/> on <see cref="Printer"/>, queued by the owner, with the file still stored.</summary>
    public async Task AddFinishedPrintAsync()
    {
        await UploadAsync();

        Print = await AddPrintAsync(Owner.Id, PrintState.Finished, ended: true);
    }

    /// <summary>An open print on <see cref="Printer"/>, queued by the owner.</summary>
    public async Task AddOwnOpenPrintAsync()
    {
        Print = await AddPrintAsync(Owner.Id, PrintState.Printing, ended: false);
    }

    /// <summary>An open print on <see cref="Printer"/>, queued by another member of the owner's team.</summary>
    public async Task AddTeammatesOpenPrintAsync()
    {
        _ = await TeammateAsync();

        Print = await AddPrintAsync(_teammate!.Id, PrintState.Printing, ended: false);
    }

    /// <summary>A network camera on the owner's team, added through its page so the sidecar holds its stream.</summary>
    /// <param name="camera">What the camera sends: H.264 when null, which WebRTC can be offered and MJPEG cannot.</param>
    public async Task AddCameraAsync(FakeCamera? camera = null)
    {
        await EnsureOwnerAsync();

        _sidecar.AddCamera(CameraSource, camera ?? FakeCamera.H264);

        Camera added = await CameraPage.AddNetworkCameraAsync(_factory, _ownerClient!, Owner, "minimum-scope", CameraSource);

        Camera = added.Uuid;
    }

    /// <summary>A live view of <see cref="Camera"/>, open over the owner's session until the world is disposed.</summary>
    public async Task OpenViewAsync()
    {
        await AddCameraAsync(MjpegCamera);

        View = Guid.NewGuid();

        HttpResponseMessage response = await _ownerClient!.GetAsync($"/api/v1/cameras/{Camera}/stream.mjpeg?view={View}",
                                                                    HttpCompletionOption.ResponseHeadersRead,
                                                                    TestContext.Current.CancellationToken);

        _disposables.Add(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the view is setup, not what the route verifies");
    }

    /// <summary>A fake printer registered and waiting, its code in <see cref="ClaimCode"/>.</summary>
    public async Task RegisterPrinterAsync()
    {
        await EnsureOwnerAsync();

        await using FakePrinterClient enrolling = new(PrinterIdentity.CreateRandom(), TimeProvider.System);
        using HttpClient anonymous = PrinterListener.CreateClient(_factory);

        ClaimCode = await enrolling.RegisterAsync(anonymous);
    }

    /// <summary>A client carrying a freshly minted token with exactly <paramref name="scope"/>, for the owner.</summary>
    /// <param name="scope">What the token may do; closed over implications when it is stored, as for any token.</param>
    /// <param name="slicerHeader">Sends the token in <c>X-Api-Key</c>, as PrusaSlicer does, rather than as a bearer.</param>
    public async Task<HttpClient> ScopedClientAsync(IEnumerable<Capability> scope, bool slicerHeader)
    {
        string plaintext;

        using (IServiceScope serviceScope = _factory.Services.CreateScope())
        {
            ApiTokenService tokens = serviceScope.ServiceProvider.GetRequiredService<ApiTokenService>();

            (_, plaintext) = await tokens.CreateAsync(Owner.Id, "minimum-scope", scope, TestContext.Current.CancellationToken);
        }

        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        if (slicerHeader)
        {
            client.DefaultRequestHeaders.Add(XApiKeyAuthenticationHandler.HeaderName, plaintext);
        }
        else
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintext);
        }

        return client;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        foreach (IAsyncDisposable disposable in _asyncDisposables)
        {
            await disposable.DisposeAsync();
        }

        _ownerClient?.Dispose();
        _teammateClient?.Dispose();
    }

    private void UseOwner(HSUser owner, HttpClient client)
    {
        _owner = owner;
        _ownerClient = client;
    }

    /// <summary>Another account, an <see cref="CapabilityPresets.Operator"/> on the owner's default team.</summary>
    private async Task<HttpClient> TeammateAsync()
    {
        if (_teammateClient is not null)
        {
            return _teammateClient;
        }

        await EnsureOwnerAsync();

        (HSUser teammate, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, $"teammate-{Guid.NewGuid():N}@example.com");

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

            int teamId = await context.TeamMembers
                                      .Where(member => member.UserId == Owner.Id && member.IsDefault)
                                      .Select(member => member.TeamId)
                                      .SingleAsync(TestContext.Current.CancellationToken);

            context.TeamMembers.Add(new TeamMember
            {
                TeamId = teamId,
                UserId = teammate.Id,
                Capabilities = CapabilitySet.Format(CapabilityPresets.Operator),
                IsDefault = false,
            });

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        _teammate = teammate;
        _teammateClient = client;

        return client;
    }

    private async Task<Guid> EnqueueAsAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/v1/printers/{Printer}/queue",
                                                                          new { name = FileName },
                                                                          TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created, "the queue entry is setup, not what the route verifies");

        using JsonDocument payload =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return payload.RootElement.GetProperty("printUuid").GetGuid();
    }

    /// <summary>One history row, as the queue's loop writes it.</summary>
    private async Task<Guid> AddPrintAsync(long queuedBy, PrintState state, bool ended)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        int printerId = await context.Printers
                                     .Where(printer => printer.Uuid == Printer)
                                     .Select(printer => printer.Id)
                                     .SingleAsync(TestContext.Current.CancellationToken);

        DateTimeOffset startedAt = DateTimeOffset.UtcNow.AddHours(-1);

        PrintJob job = new()
        {
            PrinterId = printerId,
            PrintUuid = Guid.NewGuid(),
            FileName = FileName,
            QueuedByUserId = queuedBy,
            State = state,
            StartedAt = startedAt,
            CommandedAt = startedAt,
            EndedAt = ended ? startedAt.AddMinutes(30) : null,
        };

        context.PrintJobs.Add(job);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return job.PrintUuid;
    }

    private string? ModelOf(int printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        return context.Printers.AsNoTracking().Single(printer => printer.Id == printerId).Model;
    }

    /// <summary>
    /// A running fake, closed when the world is. Its faults are not asserted: what a route did to the
    /// printer after the gate is other tests' subject, and this one has already said what it checks.
    /// </summary>
    private sealed class ConnectedFake : IAsyncDisposable
    {
        private readonly FakePrinterClient _fake;
        private readonly Task _run;

        public ConnectedFake(FakePrinterClient fake, Task run)
        {
            _fake = fake;
            _run = run;
        }

        public async ValueTask DisposeAsync()
        {
            await _fake.CloseAsync(TestContext.Current.CancellationToken);
            await _run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await _fake.DisposeAsync();
        }
    }
}
