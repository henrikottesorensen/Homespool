using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.Pages.Printers;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.DTO;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test.Printers;

/// <summary>
/// The registration-code "claim printer" page: redeems the code a printer is displaying, wired
/// through the same <see cref="PrusaConnectService.ClaimPrinterAsync"/> the JSON API uses.
/// </summary>
public sealed class ClaimModelTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-printers-claim-{Guid.NewGuid():N}.db");

    private HSDbContext NewContext()
    {
        DbContextOptions<HSDbContext> options = new DbContextOptionsBuilder<HSDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        return new HSDbContext(options);
    }

    private async Task<HSDbContext> MigratedContextAsync()
    {
        HSDbContext context = NewContext();
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

    private static PrusaConnectService NewService(HSDbContext context)
    {
        return new(context,
            new CodeGenerator(),
            new TokenService(),
            new TeamService(context),
            TimeProvider.System, NullLogger<PrusaConnectService>.Instance,
            Options.Create(new PrusaConnectOptions()));
    }

    private static RegisterPrinterRequestDTO PrinterRequest(string fingerprint)
    {
        return new()
        {
            SerialNumber = $"SN-{fingerprint}",
            FingerPrint = fingerprint,
            PrinterType = "1.3.5",
            Firmware = "6.4.0+11974",
        };
    }

    private static async Task<(ClaimModel model, HSUser user)> NewModelAsync(HSDbContext context, string email = "owner@example.com")
    {
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) = IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = new(IdentityTestHarness.UsernameFor(email)) { Email = email, EmailConfirmed = true };
        IdentityResult createResult = await users.CreateAsync(user, "Sup3rSecret!23");
        createResult.Succeeded.Should().BeTrue();

        context.AddDefaultTeam(user.Id, DateTimeOffset.UtcNow);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        ClaimModel model = new(NewService(context), new TeamService(context), users, new UnitOfWork(context),
            new ClaimAttemptLimiter(context, Options.Create(new PrusaConnectOptions()),
                NullLogger<ClaimAttemptLimiter>.Instance),
            NullLogger<ClaimModel>.Instance)
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
        };

        return (model, user);
    }

    /// <summary>Issues a fresh claimable code via the real registration path, matching
    /// <c>PrusaConnectServiceClaimTests</c>'s setup - a hand-hashed code would not prove the page
    /// actually drives the same lookup a real printer's poll relies on.</summary>
    private static async Task<string> SeedClaimableCodeAsync(HSDbContext context, string fingerprint)
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
        await using HSDbContext context = await MigratedContextAsync();
        (ClaimModel model, HSUser user) = await NewModelAsync(context);

        Team usableOnly = new() { CreatedBy = user.Id, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(usableOnly);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TeamMembers.Add(new TeamMember
        {
            TeamId = usableOnly.Id,
            UserId = user.Id,
            CanRead = true,
            CanUse = true,
            CanManage = false,
            IsDefault = false,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await model.OnGetAsync(CancellationToken.None);

        // Assert
        model.TeamOptions.Should().HaveCount(1, "the default team is CanManage; the second team is CanUse only");
    }

    // ---------- OnPostAsync ----------

    /// <summary>The happy path: a valid code claims the printer and redirects to the list with a
    /// success-styled status message, since there is no secret to lose on redirect.</summary>
    [Fact]
    public async Task OnPostAsyncClaimsThePrinterAndRedirectsWithASuccessMessage()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
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

        PrusaConnectRegistration registration = await context.PrusaConnectRegistrations.SingleAsync(TestContext.Current.CancellationToken);
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
        await using HSDbContext context = await MigratedContextAsync();
        (ClaimModel model, _) = await NewModelAsync(context);

        string code = await SeedClaimableCodeAsync(context, "FP-CASING");
        model.Input.Code = $"  {code.ToLowerInvariant()}  ";

        // Act
        IActionResult result = await model.OnPostAsync(CancellationToken.None);

        // Assert
        result.Should().BeOfType<RedirectToPageResult>();
        (await context.Printers.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    /// <summary>An unknown code is rejected without creating a printer.</summary>
    [Fact]
    public async Task OnPostAsyncWithAnUnknownCodeShowsAnError()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (ClaimModel model, _) = await NewModelAsync(context);
        model.Input.Code = "NEVER-ISSUED-CODE";

        // Act
        IActionResult result = await model.OnPostAsync(CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        model.ModelState.IsValid.Should().BeFalse();
        (await context.Printers.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    /// <summary>A code already claimed by someone else is rejected, and no competing printer is created.</summary>
    [Fact]
    public async Task OnPostAsyncWithAnAlreadyClaimedCodeShowsAnError()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (ClaimModel first, _) = await NewModelAsync(context, "first@example.com");
        string code = await SeedClaimableCodeAsync(context, "FP-DOUBLE-CLAIM");
        first.Input.Code = code;
        await first.OnPostAsync(CancellationToken.None);

        (ClaimModel second, _) = await NewModelAsync(context, "second@example.com");
        second.Input.Code = code;

        // Act
        IActionResult result = await second.OnPostAsync(CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        second.ModelState.IsValid.Should().BeFalse();
        (await context.Printers.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1, "the second claim must not create a competing printer");
    }

    /// <summary>A team the caller cannot manage is rejected, and nothing is created.</summary>
    [Fact]
    public async Task OnPostAsyncRejectsATeamTheCallerCannotManage()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (ClaimModel model, _) = await NewModelAsync(context);

        Team someoneElses = new() { CreatedBy = 999, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(someoneElses);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        string code = await SeedClaimableCodeAsync(context, "FP-WRONG-TEAM");
        model.Input.Code = code;
        model.Input.TeamId = someoneElses.Id;

        // Act
        IActionResult result = await model.OnPostAsync(CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        model.ModelState.IsValid.Should().BeFalse();
        (await context.Printers.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    /// <summary>An empty code fails validation before the service (and the database) are ever touched.</summary>
    [Fact]
    public async Task OnPostAsyncWithAnEmptyCodeFailsValidationWithoutCallingTheService()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
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
