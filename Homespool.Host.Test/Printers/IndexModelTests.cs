using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Authorisation;
using Homespool.Host.Certificates;
using Homespool.Host.Localisation;
using Homespool.Host.Pages.Printers;
using Homespool.Host.PrintFiles;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Queue;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test.Printers;

/// <summary>
/// The printer list: scoping to teams the user can read, enrolment status and live readings per
/// card, and the regenerate action for a still-unbound USB-key token.
/// </summary>
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
                 Justification = "TestTelemetryContext.For builds a second context over the same SQLite file the "
                                 + "test already owns and deletes in Dispose. EF opens and closes the connection per "
                                 + "query, so nothing is held between them, and a per-call context is what keeps each "
                                 + "read free of another one's change tracking.")]
public sealed class IndexModelTests : IDisposable
{
    private static readonly QueueSignal QueueSignal = new();

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-printers-index-{Guid.NewGuid():N}.db");

    // A reissue offers the names the certificate covers, so these tests need a real one.
    private readonly string _certificateRoot = Path.Combine(Path.GetTempPath(), $"ps-printers-index-certs-{Guid.NewGuid():N}");

    private HomespoolDbContext NewContext()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        return new HomespoolDbContext(options);
    }

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        HomespoolDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        if (Directory.Exists(_certificateRoot))
        {
            Directory.Delete(_certificateRoot, recursive: true);
        }
    }

    private static Printer NewPrinter(int teamId, string? name = null)
    {
        return new()
        {
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = teamId,
            Name = name,
            Status = PrinterStatus.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private Task<(IndexModel model, HSUser user, Team team)> NewModelAsync(HomespoolDbContext context)
    {
        return NewModelAsync(context, connectionRegistry: null);
    }

    private async Task<(IndexModel model, HSUser user, Team team)> NewModelAsync(
        HomespoolDbContext context,
        PrinterConnectionRegistry? connectionRegistry)
    {
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) = IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = new("owner") { Email = "owner@example.com", EmailConfirmed = true };
        IdentityResult createResult = await users.CreateAsync(user, "Sup3rSecret!23");
        createResult.Succeeded.Should().BeTrue();

        Team team = context.AddDefaultTeam(user.Id, DateTimeOffset.UtcNow);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        PrusaConnectOptions options = new() { PrinterHost = "printers.example.com" };

        connectionRegistry ??= new PrinterConnectionRegistry(NullLogger<PrinterConnectionRegistry>.Instance);

        PrinterCertificateAuthority authority = new(
            Options.Create(new CertificateOptions { Directory = "certs", AuthorityPassphrase = "unit test passphrase" }),
            new HostEnvironmentAccessor(_certificateRoot),
            TimeProvider.System,
            NullLogger<PrinterCertificateAuthority>.Instance,
            new PrinterLeafChangeToken());

        // A leaf covering the configured address, since that is what decides which names the reissue
        // may offer - and whether it can offer a bundle at all.
        authority.EnsureLeaf([options.PrinterHost]);

        // One localiser for both the page and its status seam - the application resolves a single
        // scoped instance, and two here would let them disagree about what is registered.
        IStringLocalizer<SharedResource> localiser =
            new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider()
                                   .GetRequiredService<IStringLocalizer<SharedResource>>();

        // The page counts each printer's queue, so it needs the real service - and that needs a file
        // store. Rooted in a temp directory nothing here writes to: an empty queue is the backdrop
        // every case below wants.
        string storeRoot = Path.Combine(Path.GetTempPath(), "homespool-printers-index-" + Guid.NewGuid().ToString("N"));
        UserFileStore store = new(TestOptions.Monitor(new PrintFileStorageOptions { Directory = storeRoot }),
                                  new HostEnvironmentAccessor(storeRoot),
                                  TimeProvider.System,
                                  NullLogger<UserFileStore>.Instance);

        PrinterAccessService access = new(context, NullLogger<PrinterAccessService>.Instance);

        IndexModel model = new(
            new PrinterQueryService(context, TestTelemetryContext.For(context), access, new TeamCapabilityLookup(context), TimeProvider.System),
            new PrusaConnectService(context, new CodeGenerator(), new TokenService(), new TeamService(context),
                                    TimeProvider.System, NullLogger<PrusaConnectService>.Instance, TestOptions.Monitor(options)),
            new DefaultPrinterService(access, users),
            new ProvisioningBundleBuilder(TestOptions.Monitor(options), Options.Create(new CertificateOptions()), authority,
                                          new DnsHostAddressResolver(), TestLocaliser.Shared()),
            new TeamService(context),
            new PrintQueueService(context, access,
                                  new PrintFileCatalog(store, context, NullLogger<PrintFileCatalog>.Instance),
                                  TimeProvider.System,
                                  QueueSignal),
            users,
            TestOptions.Snapshot(options),
            connectionRegistry,
            new PrinterStatusText(localiser),
            localiser)
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
        };

        return (model, user, team);
    }

    // ---------- OnGetAsync ----------

    /// <summary>
    /// Each printer's status reflects which table its credential actually lives in: enrolled,
    /// awaiting a USB connection, or neither (a code-exchange claim nobody has polled yet).
    /// </summary>
    [Fact]
    public async Task OnGetAsyncReportsEnrolmentStatusPerPrinter()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (IndexModel model, _, Team team) = await NewModelAsync(context);

        Printer enrolled = NewPrinter(team.Id, "Enrolled printer");
        Printer provisioned = NewPrinter(team.Id, "Provisioned printer");
        Printer neither = NewPrinter(team.Id, "Freshly claimed printer");

        context.Printers.AddRange(enrolled, provisioned, neither);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        TokenService tokenService = new();

        context.PrusaConnectAuthentication.Add(new PrusaConnectAuthenticationData
        {
            PrinterId = enrolled.Id,
            FingerPrintKey = "fp-enrolled",
            HashedToken = tokenService.HashToken(tokenService.GenerateToken()),
            EnrolledAt = DateTimeOffset.UtcNow,
        });

        context.PrusaConnectProvisionings.Add(new PrusaConnectProvisioning
        {
            PrinterId = provisioned.Id,
            HashedToken = tokenService.HashToken(tokenService.GenerateToken()),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await model.OnGetAsync(CancellationToken.None);

        // Assert
        model.Printers.Should().HaveCount(3);

        model.Printers.Single(r => r.Printer.Id == enrolled.Id).Enrolled.Should().BeTrue();
        model.Printers.Single(r => r.Printer.Id == provisioned.Id).AwaitingUsbProvisioning.Should().BeTrue();

        IndexModel.PrinterRow neitherRow = model.Printers.Single(r => r.Printer.Id == neither.Id);
        neitherRow.Enrolled.Should().BeFalse();
        neitherRow.AwaitingUsbProvisioning.Should().BeFalse();
    }

    /// <summary>A printer in a team the caller cannot even read does not appear.</summary>
    [Fact]
    public async Task OnGetAsyncDoesNotListPrintersOutsideTheCallersTeams()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (IndexModel model, _, _) = await NewModelAsync(context);

        Team othersTeam = new() { CreatedBy = 999, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(othersTeam);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Printers.Add(NewPrinter(othersTeam.Id, "Someone else's printer"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await model.OnGetAsync(CancellationToken.None);

        // Assert
        model.Printers.Should().BeEmpty();
    }

    // ---------- OnPostRegenerateAsync ----------

    /// <summary>A successful regenerate shows the new snippet once and does not redirect.</summary>
    [Fact]
    public async Task OnPostRegenerateAsyncReissuesAndShowsTheSnippetOnce()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (IndexModel model, HSUser user, Team team) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        TokenService tokenService = new();
        string originalToken = tokenService.GenerateToken();

        context.PrusaConnectProvisionings.Add(new PrusaConnectProvisioning
        {
            PrinterId = printer.Id,
            HashedToken = tokenService.HashToken(originalToken),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await model.OnPostRegenerateAsync(printer.Id, CancellationToken.None);

        // Assert
        model.RegeneratedPrinterId.Should().Be(printer.Id);
        model.Offer.Should().NotBeNull();

        string reissuedToken = model.Offer!.Snippet.Split("token = ")[1].Trim();
        reissuedToken.Should().NotBe(originalToken);

        PrusaConnectProvisioning stored =
            await context.PrusaConnectProvisionings.SingleAsync(TestContext.Current.CancellationToken);
        tokenService.VerifyToken(reissuedToken, stored.HashedToken).Should().BeTrue();
        tokenService.VerifyToken(originalToken, stored.HashedToken).Should().BeFalse("the old token must stop working");
    }

    /// <summary>An unknown printer id sets a status message rather than throwing out of the handler.</summary>
    [Fact]
    public async Task OnPostRegenerateAsyncForAnUnknownPrinterSetsAStatusMessage()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (IndexModel model, _, _) = await NewModelAsync(context);

        // Act
        await model.OnPostRegenerateAsync(printerId: 999, CancellationToken.None);

        // Assert
        model.StatusMessage.Should().NotBeNullOrEmpty();
        model.Offer.Should().BeNull();
    }

    /// <summary>A printer the caller cannot manage is refused, without leaking whether it exists.</summary>
    [Fact]
    public async Task OnPostRegenerateAsyncForAPrinterTheCallerCannotManageSetsAStatusMessage()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (IndexModel model, _, _) = await NewModelAsync(context);

        Team othersTeam = new() { CreatedBy = 999, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(othersTeam);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Printer printer = NewPrinter(othersTeam.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.PrusaConnectProvisionings.Add(new PrusaConnectProvisioning
        {
            PrinterId = printer.Id,
            HashedToken = new TokenService().HashToken(new TokenService().GenerateToken()),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await model.OnPostRegenerateAsync(printer.Id, CancellationToken.None);

        // Assert
        model.StatusMessage.Should().NotBeNullOrEmpty();
        model.Offer.Should().BeNull();
    }

    /// <summary>
    /// An already-enrolled printer can be reissued a USB token: that is how an operator puts a fresh
    /// credential onto a printer that is already connected, and the snippet is rendered for it exactly
    /// as it is for one awaiting first contact.
    /// </summary>
    /// <remarks>
    /// Adding the printer again from scratch would mint a second printer whose token the auth handler
    /// refuses to bind (it cannot tell that from an attempt on someone else's printer), so this path
    /// existing is what makes re-provisioning possible at all.
    /// </remarks>
    [Fact]
    public async Task OnPostRegenerateAsyncForAnAlreadyEnrolledPrinterRendersASnippet()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (IndexModel model, _, Team team) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.PrusaConnectAuthentication.Add(new PrusaConnectAuthenticationData
        {
            PrinterId = printer.Id,
            FingerPrintKey = "fp-already-enrolled",
            HashedToken = new TokenService().HashToken(new TokenService().GenerateToken()),
            EnrolledAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await model.OnPostRegenerateAsync(printer.Id, CancellationToken.None);

        // Assert
        model.StatusMessage.Should().BeNullOrEmpty();
        model.Offer.Should().NotBeNull();
        model.RegeneratedPrinterId.Should().Be(printer.Id);
    }

    // ---------- The card's live readings ----------

    /// <summary>
    /// A connected printer's card carries its progress, the time left and its filament, read from the
    /// live state - and a chamber reading earns it the enclosed drawing.
    /// </summary>
    [Fact]
    public async Task OnGetAsyncCarriesAConnectedPrintersLiveReadings()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        PrinterConnectionRegistry registry = new(NullLogger<PrinterConnectionRegistry>.Instance);
        (IndexModel model, _, Team team) = await NewModelAsync(context, registry);

        Printer printer = NewPrinter(team.Id, "Boxed");
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        SeedLiveState(context, printer.Id, chamber: 31.5f, progress: 42, timeRemaining: 4320, material: "PLA");

        // Connected means the link is open, not merely registered - a substitute answers false to
        // everything until told otherwise.
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        registry.Register(printer.Id, actor, overPlaintext: false);

        // Act
        await model.OnGetAsync(CancellationToken.None);

        // Assert
        IndexModel.PrinterRow row = model.Printers.Single();
        row.Connected.Should().BeTrue();
        row.FormFactor.Should().Be(Pages.PrinterFormFactor.Enclosed);
        row.Progress.Should().Be(42);
        row.TimeRemaining.Should().Be(4320);
        row.Material.Should().Be("PLA");
        row.QueuedCount.Should().Be(0);
    }

    /// <summary>
    /// <b>A disconnected printer keeps its filament and loses its progress.</b> The live state
    /// persists, so every reading on it outlives the connection; a percentage on a printer nobody can
    /// reach is a frozen reading, while what filament is loaded stays true with the power off. The
    /// front page's tiles follow the same rule, and this is what keeps the two pages agreeing.
    /// </summary>
    [Fact]
    public async Task OnGetAsyncDropsProgressForADisconnectedPrinterButKeepsItsFilament()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (IndexModel model, _, Team team) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id, "Switched off");
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        SeedLiveState(context, printer.Id, chamber: null, progress: 42, timeRemaining: 4320, material: "PETG");

        // Act
        await model.OnGetAsync(CancellationToken.None);

        // Assert
        IndexModel.PrinterRow row = model.Printers.Single();
        row.Connected.Should().BeFalse();
        row.FormFactor.Should().Be(Pages.PrinterFormFactor.Open);
        row.Progress.Should().BeNull("progress on a printer nobody can reach is a frozen reading");
        row.TimeRemaining.Should().BeNull();
        row.Material.Should().Be("PETG", "what is loaded stays true while the power is off");
    }

    private static void SeedLiveState(HomespoolDbContext context,
                                      int printerId,
                                      float? chamber,
                                      int? progress,
                                      int? timeRemaining,
                                      string? material)
    {
        context.PrinterLiveStates.Add(new PrinterLiveState
        {
            PrinterId = printerId,
            NozzleTemperature = 25,
            ChamberTemperature = chamber,
            Progress = progress,
            TimeRemaining = timeRemaining,
            Material = material,
        });

        context.SaveChanges();
    }
}
