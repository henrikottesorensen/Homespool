using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Localisation;
using Homespool.Host.Pages.Printers;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.DTO;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test.Printers;

/// <summary>
/// The registration-code "claim printer" page: redeems the code a printer is displaying, wired
/// through the same <see cref="PrusaConnectService.ClaimPrinterAsync"/> the JSON API uses.
/// </summary>
public sealed class ClaimModelTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-printers-claim-{Guid.NewGuid():N}.db");

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
    }

    private static PrusaConnectService NewService(HomespoolDbContext context)
    {
        return new(context,
                   new CodeGenerator(),
                   new TokenService(),
                   new TeamService(context),
                   TimeProvider.System, NullLogger<PrusaConnectService>.Instance,
                   TestOptions.Monitor(new PrusaConnectOptions()));
    }

    private static RegisterPrinterRequestDTO PrinterRequest(string fingerprint,
                                                            string printerType = "1.3.5",
                                                            string firmware = "6.4.0+11974")
    {
        return new()
        {
            SerialNumber = $"SN-{fingerprint}",
            FingerPrint = fingerprint,
            PrinterType = printerType,
            Firmware = firmware,
        };
    }

    private static async Task<(ClaimModel model, HSUser user)> NewModelAsync(HomespoolDbContext context,
                                                                             string email = "owner@example.com")
    {
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) = IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = new(IdentityTestHarness.UsernameFor(email)) { Email = email, EmailConfirmed = true };
        IdentityResult createResult = await users.CreateAsync(user, "Sup3rSecret!23");
        createResult.Succeeded.Should().BeTrue();

        context.AddDefaultTeam(user.Id, DateTimeOffset.UtcNow);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        RegistrationCodeClaim claim = new(NewService(context), new UnitOfWork(context),
                                          new AttemptLimiter(context, TestOptions.Snapshot(new AttemptLimitOptions()),
                                                             NullLogger<AttemptLimiter>.Instance),
                                          TimeProvider.System);

        ClaimModel model = new(claim, NewService(context), new TeamService(context), users,
                               new RelativeTimeText(TestLocaliser.Shared()),
                               TimeProvider.System,
                               NullLogger<ClaimModel>.Instance,
                               TestLocaliser.Shared())
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
        };

        return (model, user);
    }

    /// <summary>The claim attempts counted against <paramref name="user"/>, or 0 when there is no row.</summary>
    private static async Task<int> ClaimAttemptsAsync(HomespoolDbContext context, HSUser user)
    {
        return await context.UserActionAttempts.AsNoTracking()
                            .Where(a => a.UserId == user.Id && a.Action == LimitedAction.ClaimPrinter)
                            .Select(a => a.FailedCount)
                            .SingleOrDefaultAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Takes <see cref="Capability.ManagePrinter"/> away from <paramref name="user"/>'s default team.</summary>
    private static async Task DemoteToOperatorAsync(HomespoolDbContext context, HSUser user)
    {
        TeamMember membership = await context.TeamMembers.SingleAsync(m => m.UserId == user.Id, TestContext.Current.CancellationToken);
        membership.Capabilities = TestMemberships.Literal(CapabilityPresets.Operator);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The one page-level error the last post left behind.</summary>
    private static string PageError(ClaimModel model)
    {
        return model.ModelState[string.Empty]!.Errors.Should().ContainSingle().Subject.ErrorMessage;
    }

    /// <summary>Issues a fresh claimable code via the real registration path, matching
    /// <c>PrusaConnectServiceClaimTests</c>'s setup - a hand-hashed code would not prove the page
    /// actually drives the same lookup a real printer's poll relies on.</summary>
    private static async Task<string> SeedClaimableCodeAsync(HomespoolDbContext context, string fingerprint)
    {
        CodeResponseDTO response = await NewService(context).GetPrinterCode(PrinterRequest(fingerprint));

        return response.TemporaryCode;
    }

    // ---------- OnGetAsync ----------

    /// <summary>Only teams the user can manage appear - identical bar to the USB-key Add page.</summary>
    [Fact]
    public async Task OnGetAsyncListsOnlyTeamsTheUserCanManage()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ClaimModel model, HSUser user) = await NewModelAsync(context);

        Team usableOnly = new() { CreatedBy = user.Id, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(usableOnly);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TeamMembers.Add(new TeamMember
        {
            TeamId = usableOnly.Id,
            UserId = user.Id,
            Capabilities = TestMemberships.Literal(CapabilityPresets.Operator),
            IsDefault = false,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await model.OnGetAsync(CancellationToken.None);

        // Assert
        model.TeamOptions.Should().HaveCount(1, "the default team grants ManagePrinter; the second only Print and ControlPrinter");
    }

    /// <summary>
    /// A waiting printer is listed by the name the firmware gives its model, with its version and how
    /// long ago it asked.
    /// </summary>
    [Fact]
    public async Task OnGetAsyncListsAWaitingPrinterByItsModelName()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ClaimModel model, _) = await NewModelAsync(context);
        await NewService(context).GetPrinterCode(PrinterRequest("FP-WAITING", printerType: "3.1.1", firmware: "6.4.0+11974"));

        // Act
        await model.OnGetAsync(CancellationToken.None);

        // Assert
        model.ShowsPending.Should().BeTrue();
        ClaimModel.PendingPrinter printer = model.Pending.Should().ContainSingle().Subject;
        printer.Model.Should().Be("XL+", "the name the printer shows on its own screen, not its id");
        printer.Firmware.Should().Be("6.4.0");
        printer.Age.Should().Be(TestLocaliser.Shared()["Common_JustNow"].Value);
    }

    /// <summary>
    /// A model no table knows is shown as unknown rather than as the string that arrived - the POST
    /// behind it is anonymous, so that string is whatever a stranger chose.
    /// </summary>
    [Fact]
    public async Task OnGetAsyncShowsAModelItDoesNotKnowAsUnknown()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ClaimModel model, _) = await NewModelAsync(context);
        await NewService(context).GetPrinterCode(PrinterRequest("FP-STRANGER", printerType: "Your printer - call 555-0100"));

        // Act
        await model.OnGetAsync(CancellationToken.None);

        // Assert
        model.Pending.Should().ContainSingle()
             .Which.Model.Should().Be(TestLocaliser.Shared()["Printers_PendingUnknownModel"].Value);
    }

    /// <summary>
    /// The firmware shown is rebuilt from the version it parsed to, so nothing a stranger wrote after
    /// the numbers reaches the page, and a string that is no version at all shows nothing.
    /// </summary>
    [Theory]
    [InlineData("6.4.0+11974", "6.4.0")]
    [InlineData("6.5.7+ go to evil.example to finish", "6.5.7")]
    [InlineData("go to evil.example to finish", null)]
    public async Task OnGetAsyncShowsOnlyTheVersionItCanRead(string stated, string? shown)
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ClaimModel model, _) = await NewModelAsync(context);
        await NewService(context).GetPrinterCode(PrinterRequest("FP-FIRMWARE", firmware: stated));

        // Act
        await model.OnGetAsync(CancellationToken.None);

        // Assert
        model.Pending.Should().ContainSingle().Which.Firmware.Should().Be(shown);
    }

    /// <summary>
    /// Somebody who could not add a printer to any team is not shown the ones waiting: a pending
    /// registration belongs to no team, so this is the only line drawn around it.
    /// </summary>
    [Fact]
    public async Task OnGetAsyncListsNothingToSomebodyWhoCannotAddAPrinter()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ClaimModel model, HSUser user) = await NewModelAsync(context);
        await DemoteToOperatorAsync(context, user);
        await NewService(context).GetPrinterCode(PrinterRequest("FP-HIDDEN"));

        // Act
        await model.OnGetAsync(CancellationToken.None);

        // Assert
        model.ShowsPending.Should().BeFalse();
        model.Pending.Should().BeEmpty();
    }

    // ---------- OnPostAsync ----------

    /// <summary>The happy path: a valid code claims the printer and redirects to the list with a
    /// success-styled status message, since there is no secret to lose on redirect.</summary>
    [Fact]
    public async Task OnPostAsyncClaimsThePrinterAndRedirectsWithASuccessMessage()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ClaimModel model, _) = await NewModelAsync(context);

        string code = await SeedClaimableCodeAsync(context, "FP-HAPPY-PATH");
        model.Input.Code = code;
        model.Input.Name = "Bench printer";
        model.Input.Location = "Workshop";

        // Act
        IActionResult result = await model.OnPostAsync(CancellationToken.None);

        // Assert
        RedirectToPageResult redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
        redirect.PageName.Should().Be("Index");

        Printer printer = await context.Printers.SingleAsync(TestContext.Current.CancellationToken);
        printer.Name.Should().Be("Bench printer");

        PrusaConnectRegistration registration =
            await context.PrusaConnectRegistrations.SingleAsync(TestContext.Current.CancellationToken);
        registration.PrinterId.Should().Be(printer.Id);

        model.StatusSuccess.Should().BeTrue();
        model.StatusMessage.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// A code typed with different casing and stray whitespace - the shape a human copying off a
    /// printer's screen actually produces - still claims successfully. Regression test: the
    /// TemporaryCode lookup has no case-insensitive collation, so this silently failed before the
    /// page normalised the input.
    /// </summary>
    [Fact]
    public async Task OnPostAsyncNormalisesCodeCasingAndWhitespace()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ClaimModel model, _) = await NewModelAsync(context);

        string code = await SeedClaimableCodeAsync(context, "FP-CASING");
        model.Input.Code = $"  {code.ToLowerInvariant()}  ";

        // Act
        IActionResult result = await model.OnPostAsync(CancellationToken.None);

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        (await context.Printers.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    /// <summary>An unknown code is rejected without creating a printer, and counted as a guess.</summary>
    [Fact]
    public async Task OnPostAsyncWithAnUnknownCodeShowsAnError()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ClaimModel model, HSUser user) = await NewModelAsync(context);
        model.Input.Code = "NEVER-ISSUED-CODE";

        // Act
        IActionResult result = await model.OnPostAsync(CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        model.ModelState.IsValid.Should().BeFalse();
        (await context.Printers.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
        (await ClaimAttemptsAsync(context, user)).Should().Be(1, "a code nobody issued is the one outcome that is a guess");
    }

    /// <summary>
    /// A wrong code with nothing waiting says so: the code cannot have come from this server, which
    /// is a different problem from a mistyped one.
    /// </summary>
    [Fact]
    public async Task OnPostAsyncWithNothingWaitingSaysNoPrinterIsWaiting()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ClaimModel model, _) = await NewModelAsync(context);
        model.Input.Code = "ZZZZZZZZZZ";

        // Act
        await model.OnPostAsync(CancellationToken.None);

        // Assert
        PageError(model).Should().Be(TestLocaliser.Shared()["Printers_ClaimNoSuchCodeNothingWaiting"].Value);
    }

    /// <summary>A wrong code while a printer is waiting points at the code instead.</summary>
    [Fact]
    public async Task OnPostAsyncWithAPrinterWaitingSaysToCheckTheCode()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ClaimModel model, _) = await NewModelAsync(context);
        await SeedClaimableCodeAsync(context, "FP-WAITING-FOR-SOMEONE");
        model.Input.Code = "ZZZZZZZZZZ";

        // Act
        await model.OnPostAsync(CancellationToken.None);

        // Assert
        PageError(model).Should().Be(TestLocaliser.Shared()["Printers_ClaimNoSuchCode"].Value);
    }

    /// <summary>
    /// Somebody not shown the list is not told through the error whether it is empty either.
    /// </summary>
    [Fact]
    public async Task OnPostAsyncDoesNotTellSomebodyWhoCannotAddAPrinterWhetherAnyAreWaiting()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ClaimModel model, HSUser user) = await NewModelAsync(context);
        await DemoteToOperatorAsync(context, user);
        model.Input.Code = "ZZZZZZZZZZ";

        // Act
        await model.OnPostAsync(CancellationToken.None);

        // Assert
        PageError(model).Should().Be(TestLocaliser.Shared()["Printers_ClaimNoSuchCode"].Value);
    }

    /// <summary>
    /// A code already claimed by someone else is rejected, and no competing printer is created. The code
    /// was right, so the attempt it was counted as before the lookup is given back.
    /// </summary>
    [Fact]
    public async Task OnPostAsyncWithAnAlreadyClaimedCodeShowsAnError()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ClaimModel first, _) = await NewModelAsync(context, "first@example.com");
        string code = await SeedClaimableCodeAsync(context, "FP-DOUBLE-CLAIM");
        first.Input.Code = code;
        await first.OnPostAsync(CancellationToken.None);

        (ClaimModel second, HSUser secondUser) = await NewModelAsync(context, "second@example.com");
        second.Input.Code = code;

        // Act
        IActionResult result = await second.OnPostAsync(CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        second.ModelState.IsValid.Should().BeFalse();
        (await context.Printers.CountAsync(TestContext.Current.CancellationToken)).Should()
                                                                                  .Be(
                                                                                      1,
                                                                                      "the second claim must not create a competing printer");
        (await ClaimAttemptsAsync(context, secondUser)).Should().Be(0, "a right code is not a guess, whatever else refused it");
    }

    /// <summary>A team the caller cannot manage is rejected, nothing is created, and the right code costs no attempt.</summary>
    [Fact]
    public async Task OnPostAsyncRejectsATeamTheCallerCannotManage()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ClaimModel model, HSUser user) = await NewModelAsync(context);

        Team someoneElses = new() { CreatedBy = 999, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(someoneElses);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        string code = await SeedClaimableCodeAsync(context, "FP-WRONG-TEAM");
        model.Input.Code = code;
        model.Input.TeamUuid = someoneElses.Uuid;

        // Act
        IActionResult result = await model.OnPostAsync(CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        model.ModelState.IsValid.Should().BeFalse();
        (await context.Printers.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
        (await ClaimAttemptsAsync(context, user)).Should().Be(0, "claiming into the wrong team with the right code is not a guess");
    }

    /// <summary>An empty code fails validation before the service (and the database) are ever touched.</summary>
    [Fact]
    public async Task OnPostAsyncWithAnEmptyCodeFailsValidationWithoutCallingTheService()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (ClaimModel model, _) = await NewModelAsync(context);
        model.Input.Code = string.Empty;
        model.ModelState.AddModelError("Input.Code", "Enter the code shown on the printer's screen.");

        // Act
        IActionResult result = await model.OnPostAsync(CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        (await context.Printers.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
        (await context.PrusaConnectRegistrations.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }
}
