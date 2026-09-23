using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The restart page against a real host: offered only where something will start the service again,
/// warning of what it would interrupt, and stopping the host once it has answered.
/// </summary>
/// <remarks>
/// <b>The stop is the real one.</b> The host here is built per test, so the page is allowed to stop it,
/// and the test waits on the same <see cref="IHostApplicationLifetime"/> signal Docker's restart would
/// follow - a stand-in would prove only that the stand-in was called.
/// </remarks>
public sealed class RestartPageTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("restart");
    private readonly CapturingSink _log = new();
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch, messageDispatcher: null, _log);

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }

    [Fact]
    public async Task AnOrdinaryAccountCannotReachIt()
    {
        Start(inContainer: true);

        (HSUser _, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, "ordinary-restart@example.com");

        using HttpResponseMessage response = await client.GetAsync("/Admin/Restart", TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);

        client.Dispose();
    }

    /// <summary>
    /// Under <c>dotnet run</c> nothing would start the service again, so the button would only stop it.
    /// </summary>
    [Fact]
    public async Task OutsideAContainerThereIsNoRestart()
    {
        Start(inContainer: false);

        HttpClient admin = await AdminAsync("restart-bare@example.com");

        string menu = await admin.GetStringAsync("/Admin/Settings", TestContext.Current.CancellationToken);

        menu.Should().NotContain("id=\"restart\"");

        using HttpResponseMessage page = await admin.GetAsync("/Admin/Restart", TestContext.Current.CancellationToken);

        page.StatusCode.Should().Be(HttpStatusCode.NotFound);

        admin.Dispose();
    }

    [Fact]
    public async Task InsideAContainerTheAdminMenuOffersIt()
    {
        Start(inContainer: true);

        HttpClient admin = await AdminAsync("restart-menu@example.com");

        string menu = await admin.GetStringAsync("/Admin/Settings", TestContext.Current.CancellationToken);

        menu.Should().Contain("id=\"restart\"");

        string page = await admin.GetStringAsync("/Admin/Restart", TestContext.Current.CancellationToken);

        page.Should().Contain("Nothing is in progress that a restart would interrupt.");
        page.Should().NotContain("Telemetry is held in memory", "this host keeps telemetry on disk");

        admin.Dispose();
    }

    /// <summary>
    /// The page answers first, saying it is restarting and where it will go, and only then does the
    /// host stop - with who asked in the log.
    /// </summary>
    [Fact]
    public async Task RestartingAnswersThenStopsTheService()
    {
        Start(inContainer: true);

        HSUser administrator;
        HttpClient admin;
        (administrator, admin) = await AdminWithAccountAsync("restart-go@example.com");

        IHostApplicationLifetime lifetime = _factory.Services.GetRequiredService<IHostApplicationLifetime>();
        TaskCompletionSource stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenRegistration registration = lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());

        using HttpResponseMessage response = await PostAsync(admin);

        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("data-await-restart=\"/health/live\"");
        body.Should().Contain("<script src=\"/js/await-restart.", "the page that waits has to load the script that does the waiting");

        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        _log.CountEventsWith(("AdministratorId", administrator.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Should().Be(1, "one restart, logged once with who asked");

        admin.Dispose();
    }

    /// <summary>
    /// Offers live only in this process, so a restart breaks a transfer - named by file and printer,
    /// and warned of rather than refused, since the person pressing it can choose to wait.
    /// </summary>
    [Fact]
    public async Task ATransferInProgressIsNamedAndDoesNotBlock()
    {
        Start(inContainer: true);

        (HSUser administrator, HttpClient admin) = await AdminWithAccountAsync("restart-transfer@example.com");

        int printer = SeedPrinter(administrator.Id, "Workshop MK4", status: null);

        TransferOfferStore offers = _factory.Services.GetRequiredService<TransferOfferStore>();
        offers.Offer("0123456789abcdef", await WriteFileAsync("bracket.gcode"), printer).Should().BeTrue();
        offers.Offer("fedcba9876543210", await WriteFileAsync("orphan.gcode"), int.MaxValue).Should().BeTrue();

        string page = await admin.GetStringAsync("/Admin/Restart", TestContext.Current.CancellationToken);

        page.Should().Contain("A restart breaks these file transfers:");
        page.Should().Contain("bracket.gcode to Workshop MK4");
        page.Should().Contain("orphan.gcode, to a printer since removed", "a transfer is not dropped from the warning for want of a name");
        page.Should().Contain("Restart now", "a warning, not a refusal");

        admin.Dispose();
    }

    /// <summary>
    /// A printer's last-known state outlives its connection, so only one that is still connected is
    /// counted as a print a restart would miss.
    /// </summary>
    [Fact]
    public async Task OnlyAConnectedPrinterCountsAsPrinting()
    {
        Start(inContainer: true);

        (HSUser administrator, HttpClient admin) = await AdminWithAccountAsync("restart-printing@example.com");

        int connected = SeedPrinter(administrator.Id, "Connected", PrinterStatus.Printing);
        SeedPrinter(administrator.Id, "Switched off", PrinterStatus.Printing);

        _factory.Services.GetRequiredService<PrinterConnectionRegistry>()
                .Register(connected, new OpenLink(), overPlaintext: false);

        string page = await admin.GetStringAsync("/Admin/Restart", TestContext.Current.CancellationToken);

        page.Should().Contain("These printers have a print on them.");
        page.Should().Contain("<li>Connected</li>");
        page.Should().NotContain("Switched off");

        admin.Dispose();
    }

    /// <summary>
    /// With telemetry in memory a restart discards the samples and events, and the page says so. The
    /// mode is the one this process registered, not the saved setting, which may already differ.
    /// </summary>
    [Fact]
    public async Task WithTelemetryInMemoryItSaysARestartDiscardsIt()
    {
        _factory.ConfigurationOverrides["Storage:TelemetryInMemory"] = "true";
        Start(inContainer: true);

        HttpClient admin = await AdminAsync("restart-memory@example.com");

        string page = await admin.GetStringAsync("/Admin/Restart", TestContext.Current.CancellationToken);

        page.Should().Contain("Telemetry is held in memory, so a restart discards the recorded samples and events.");

        admin.Dispose();
    }

    /// <summary>
    /// Saving a setting that waits for a restart says so and links here; saving one that applies at once
    /// does neither.
    /// </summary>
    [Fact]
    public async Task SavingASettingThatWaitsForARestartLinksHere()
    {
        Start(inContainer: true);

        HttpClient admin = await AdminAsync("restart-settings@example.com");

        string live = await SaveSettingAsync(admin, "Invitations:LifetimeHours", "72");

        live.Should().Contain("Settings saved.");
        live.Should().NotContain("apply after a restart");
        live.Should().NotContain("id=\"restart-now\"");

        string restart = await SaveSettingAsync(admin, "Smtp:FromName", "Workshop");

        restart.Should().Contain("Some of these changes apply after a restart.");
        restart.Should().Contain("id=\"restart-now\"");

        admin.Dispose();
    }

    private void Start(bool inContainer)
    {
        // Said either way: a run inside a .NET container image has the variable set for real.
        _factory.ConfigurationOverrides[ApplicationRestarter.ContainerVariable] = inContainer ? "true" : "false";

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();
    }

    private async Task<HttpClient> AdminAsync(string email)
    {
        (HSUser _, HttpClient client) = await AdminWithAccountAsync(email);

        return client;
    }

    private async Task<(HSUser account, HttpClient client)> AdminWithAccountAsync(string email)
    {
        (HSUser account, HttpClient client) = await EnrolmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, email, AdminBootstrap.AdminRole);

        await EnrolmentFlowHelper.ReauthenticateAsync(client);

        return (account, client);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client)
    {
        string page = await client.GetStringAsync("/Admin/Restart", TestContext.Current.CancellationToken);

        using FormUrlEncodedContent content = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page),
        });

        return await client.PostAsync("/Admin/Restart", content, TestContext.Current.CancellationToken);
    }

    private static async Task<string> SaveSettingAsync(HttpClient client, string path, string value)
    {
        string page = await client.GetStringAsync("/Admin/Settings", TestContext.Current.CancellationToken);

        using FormUrlEncodedContent content = new(new Dictionary<string, string>
        {
            [$"Values[{path}]"] = value,
            ["__RequestVerificationToken"] = AntiforgeryTestHelper.ExtractToken(page),
        });

        using HttpResponseMessage response = await client.PostAsync("/Admin/Settings", content, TestContext.Current.CancellationToken);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> WriteFileAsync(string name)
    {
        string path = Path.Combine(_scratch.Path, name);
        await File.WriteAllBytesAsync(path, new byte[64], TestContext.Current.CancellationToken);

        return path;
    }

    private int SeedPrinter(long userId, string name, PrinterStatus? status)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        int teamId = context.TeamMembers.First(member => member.UserId == userId).TeamId;

        Printer printer = new() { Uuid = Guid.NewGuid(), TeamId = teamId, Name = name };
        context.Printers.Add(printer);
        context.SaveChanges();

        if (status is { } reported)
        {
            context.PrinterLiveStates.Add(new PrinterLiveState
            {
                PrinterId = printer.Id,
                Status = reported,
                LastSeenAt = DateTimeOffset.UtcNow,
            });
            context.SaveChanges();
        }

        return printer.Id;
    }

    /// <summary>A connection that is up and does nothing else.</summary>
    private sealed class OpenLink : IPrinterLink
    {
        public bool IsOpen => true;

        public Task<CommandSendResult> SendAsync(IPrinterIntent intent, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("these tests render a page and send the printer nothing");
        }

        public void Complete()
        {
        }
    }
}
