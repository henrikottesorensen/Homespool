using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using PrinterService.Data;
using PrinterService.Host.Pages.Printers;
using PrinterService.Host.PrusaConnect;
using PrinterService.Host.PrusaConnect.Commands;
using PrinterService.Host.Services;
using PrinterService.Model;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Test.Printers;

/// <summary>
/// The printer list: scoping to teams the user can read, enrollment status per row, and the
/// regenerate action for a still-unbound USB-key token.
/// </summary>
public sealed class IndexModelTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-printers-index-{Guid.NewGuid():N}.db");

    private PSDbContext NewContext()
    {
        DbContextOptions<PSDbContext> options = new DbContextOptionsBuilder<PSDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        return new PSDbContext(options);
    }

    private async Task<PSDbContext> MigratedContextAsync()
    {
        PSDbContext context = NewContext();
        await context.Database.MigrateAsync();

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
    }

    private static Task<(IndexModel Model, PSUser User, Team Team)> NewModelAsync(PSDbContext context) =>
        NewModelAsync(context, transport: null);

    private static async Task<(IndexModel Model, PSUser User, Team Team)> NewModelAsync(PSDbContext context, IPrinterCommandTransport? transport)
    {
        (UserManager<PSUser> users, _, DefaultHttpContext httpContext, _) = IdentityTestHarness.BuildIdentityServices(context);

        PSUser user = new() { UserName = "owner@example.com", Email = "owner@example.com", EmailConfirmed = true };
        IdentityResult createResult = await users.CreateAsync(user, "Sup3rSecret!23");
        createResult.Succeeded.Should().BeTrue();

        Team team = context.AddDefaultTeam(user.Id, DateTimeOffset.UtcNow);
        await context.SaveChangesAsync();

        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        PrusaConnectOptions options = new() { PublicHost = "printers.example.com" };
        PrinterConnectionRegistry connectionRegistry = new();

        transport ??= new PrinterCommandTransport(connectionRegistry, new PrinterCommandCorrelator(), NullLogger<PrinterCommandTransport>.Instance, Options.Create(options));

        IndexModel model = new(
            new PrinterQueryService(context),
            new PrusaConnectService(context, new CodeGenerator(), new TokenService(), new TeamService(context),
                NullLogger<PrusaConnectService>.Instance, Options.Create(options)),
            new TeamService(context),
            users,
            Options.Create(options),
            connectionRegistry,
            new PrinterCommandService(context, new TeamService(context), transport))
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
        };

        return (model, user, team);
    }

    private static Printer NewPrinter(int teamId, string? name = null) => new()
    {
        Uuid = Guid.NewGuid(),
        Type = PrinterType.PrusaConnect,
        TeamId = teamId,
        Name = name,
        Status = PrinterStatus.Unknown,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    // ---------- OnGetAsync ----------

    /// <summary>
    /// Each printer's status reflects which table its credential actually lives in: enrolled,
    /// awaiting a USB connection, or neither (a code-exchange claim nobody has polled yet).
    /// </summary>
    [Fact]
    public async Task OnGetAsyncReportsEnrollmentStatusPerPrinter()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        (IndexModel model, _, Team team) = await NewModelAsync(context);

        Printer enrolled = NewPrinter(team.Id, "Enrolled printer");
        Printer provisioned = NewPrinter(team.Id, "Provisioned printer");
        Printer neither = NewPrinter(team.Id, "Freshly claimed printer");

        context.Printers.AddRange(enrolled, provisioned, neither);
        await context.SaveChangesAsync();

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

        await context.SaveChangesAsync();

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
        await using PSDbContext context = await MigratedContextAsync();
        (IndexModel model, _, _) = await NewModelAsync(context);

        Team othersTeam = new() { CreatedBy = 999, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(othersTeam);
        await context.SaveChangesAsync();

        context.Printers.Add(NewPrinter(othersTeam.Id, "Someone else's printer"));
        await context.SaveChangesAsync();

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
        await using PSDbContext context = await MigratedContextAsync();
        (IndexModel model, PSUser user, Team team) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync();

        TokenService tokenService = new();
        string originalToken = tokenService.GenerateToken();

        context.PrusaConnectProvisionings.Add(new PrusaConnectProvisioning
        {
            PrinterId = printer.Id,
            HashedToken = tokenService.HashToken(originalToken),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync();

        // Act
        await model.OnPostRegenerateAsync(printer.Id, CancellationToken.None);

        // Assert
        model.RegeneratedPrinterId.Should().Be(printer.Id);
        model.Snippet.Should().NotBeNull();

        string reissuedToken = model.Snippet!.Split("token = ")[1].Trim();
        reissuedToken.Should().NotBe(originalToken);

        PrusaConnectProvisioning stored = await context.PrusaConnectProvisionings.SingleAsync();
        tokenService.VerifyToken(reissuedToken, stored.HashedToken).Should().BeTrue();
        tokenService.VerifyToken(originalToken, stored.HashedToken).Should().BeFalse("the old token must stop working");
    }

    /// <summary>An unknown printer id sets a status message rather than throwing out of the handler.</summary>
    [Fact]
    public async Task OnPostRegenerateAsyncForAnUnknownPrinterSetsAStatusMessage()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        (IndexModel model, _, _) = await NewModelAsync(context);

        // Act
        await model.OnPostRegenerateAsync(printerId: 999, CancellationToken.None);

        // Assert
        model.StatusMessage.Should().NotBeNullOrEmpty();
        model.Snippet.Should().BeNull();
    }

    /// <summary>A printer the caller cannot manage is refused, without leaking whether it exists.</summary>
    [Fact]
    public async Task OnPostRegenerateAsyncForAPrinterTheCallerCannotManageSetsAStatusMessage()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        (IndexModel model, _, _) = await NewModelAsync(context);

        Team othersTeam = new() { CreatedBy = 999, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(othersTeam);
        await context.SaveChangesAsync();

        Printer printer = NewPrinter(othersTeam.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync();

        context.PrusaConnectProvisionings.Add(new PrusaConnectProvisioning
        {
            PrinterId = printer.Id,
            HashedToken = new TokenService().HashToken(new TokenService().GenerateToken()),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        // Act
        await model.OnPostRegenerateAsync(printer.Id, CancellationToken.None);

        // Assert
        model.StatusMessage.Should().NotBeNullOrEmpty();
        model.Snippet.Should().BeNull();
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
        await using PSDbContext context = await MigratedContextAsync();
        (IndexModel model, _, Team team) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync();

        context.PrusaConnectAuthentication.Add(new PrusaConnectAuthenticationData
        {
            PrinterId = printer.Id,
            FingerPrintKey = "fp-already-enrolled",
            HashedToken = new TokenService().HashToken(new TokenService().GenerateToken()),
            EnrolledAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        // Act
        await model.OnPostRegenerateAsync(printer.Id, CancellationToken.None);

        // Assert
        model.StatusMessage.Should().BeNullOrEmpty();
        model.Snippet.Should().NotBeNullOrEmpty();
        model.RegeneratedPrinterId.Should().Be(printer.Id);
    }

    // ---------- OnPostPauseAsync (catch-all fallback) ----------

    /// <summary>A transport that throws whatever's given, bypassing PrinterCommandTransport's own
    /// correlator/timeout handling entirely - simulating an exception PrinterCommandService can't
    /// predict, e.g. a WebSocket write racing a disconnect.</summary>
    private sealed class ThrowingTransport(Exception exception) : IPrinterCommandTransport
    {
        public Task<CommandSendResult> SendAsync(int printerId, ISendableCommand commandData, CancellationToken cancellationToken) =>
            throw exception;
    }

    /// <summary>
    /// An exception type PrinterCommandService never throws itself - none of the typed catch clauses
    /// in OnPostPauseAsync's shared handler match it - falls through to the generic message instead
    /// of propagating as an unhandled exception.
    /// </summary>
    [Fact]
    public async Task OnPostPauseAsyncFallsBackToAGenericMessageForAnUnpredictedException()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        (IndexModel model, _, Team team) = await NewModelAsync(context, new ThrowingTransport(new InvalidOperationException("socket gone")));

        Printer printer = NewPrinter(team.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync();

        // Act
        IActionResult result = await model.OnPostPauseAsync(printer.Id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        model.StatusMessage.Should().Be("Something went wrong sending the command.");
        model.StatusSuccess.Should().BeFalse();
    }
}
