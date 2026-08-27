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
using Homespool.Host.Accounts;
using Homespool.Host.Certificates;
using Homespool.Host.Pages.Printers;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test.Printers;

/// <summary>
/// The USB-key "add printer" page: provisions a printer and shows the ini snippet exactly once.
/// </summary>
public sealed class AddModelTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-printers-add-{Guid.NewGuid():N}.db");

    // The page offers the names its certificate covers, so these tests need a real one - the leaf is
    // what decides whether a bundle can be built at all.
    private readonly string _certificateRoot = Path.Combine(Path.GetTempPath(), $"ps-printers-add-certs-{Guid.NewGuid():N}");

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

    private ProvisioningBundleBuilder NewBundleBuilder(PrusaConnectOptions options)
    {
        PrinterCertificateAuthority authority = new(
            Options.Create(new CertificateOptions { Directory = "certs" }),
            new HostEnvironmentAccessor(_certificateRoot),
            TimeProvider.System,
            NullLogger<PrinterCertificateAuthority>.Instance);

        if (options.PrinterTls && options.IsPrinterAddressConfigured)
        {
            authority.EnsureLeaf([options.PrinterHost]);
        }

        return new ProvisioningBundleBuilder(Options.Create(options), Options.Create(new CertificateOptions()), authority,
                                             new DnsHostAddressResolver(), TestLocaliser.Shared());
    }

    /// <summary>
    /// Builds an AddModel wired to real services, a signed-in user with a default team they can
    /// manage, and the fake PageContext plumbing unit-tested PageModels need.
    /// </summary>
    private async Task<(AddModel model, HSUser user)> NewModelAsync(HomespoolDbContext context,
                                                                    string publicHost = "printers.example.com")
    {
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) = IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = new("owner") { Email = "owner@example.com", EmailConfirmed = true };
        IdentityResult createResult = await users.CreateAsync(user, "Sup3rSecret!23");
        createResult.Succeeded.Should().BeTrue();

        context.AddDefaultTeam(user.Id, DateTimeOffset.UtcNow);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        PrusaConnectOptions options = new()
        {
            PrinterHost = publicHost,
            PrinterPort = 443,
            PrinterTls = true,
        };

        PrusaConnectService prusaConnectService = new(context, new CodeGenerator(), new TokenService(), new TeamService(context),
                                                      TimeProvider.System, NullLogger<PrusaConnectService>.Instance,
                                                      Options.Create(options));

        AddModel model = new(prusaConnectService, NewBundleBuilder(options), new TeamService(context), users,
                             new UnitOfWork(context),
                             Options.Create(options), TestLocaliser.Shared(), NullLogger<AddModel>.Instance)
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
        };

        return (model, user);
    }

    // ---------- OnGetAsync ----------

    /// <summary>Only teams the user can manage appear - a team they can merely use is not offered.</summary>
    [Fact]
    public async Task OnGetAsyncListsOnlyTeamsTheUserCanManage()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (AddModel model, HSUser user) = await NewModelAsync(context);

        Team usableOnly = new() { CreatedBy = user.Id, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(usableOnly);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TeamMembers.Add(new TeamMember
        {
            TeamId = usableOnly.Id,
            UserId = user.Id,
            Capabilities = TestMemberships.Graded(true, true, false),
            IsDefault = false,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await model.OnGetAsync(CancellationToken.None);

        // Assert
        model.TeamOptions.Should().HaveCount(1, "the default team is CanManage; the second team is CanUse only");
    }

    // ---------- OnPostAsync ----------

    /// <summary>
    /// Without a configured public address the submission is blocked server-side, not merely by a
    /// disabled button - a snippet with an empty hostname would be useless.
    /// </summary>
    [Fact]
    public async Task OnPostAsyncWithNoPrinterAddressConfiguredBlocksSubmission()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (AddModel model, _) = await NewModelAsync(context, publicHost: string.Empty);

        // Act
        await model.OnPostAsync(CancellationToken.None);

        // Assert
        model.Offer.Should().BeNull();
        model.ModelState.IsValid.Should().BeFalse();
        (await context.Printers.AnyAsync(TestContext.Current.CancellationToken)).Should()
                                                                                .BeFalse(
                                                                                    "nothing should be provisioned while unconfigured");
    }

    /// <summary>
    /// A valid submission provisions the printer, shows the snippet once with a token that actually
    /// verifies against what was stored, and resets the form for the next one.
    /// </summary>
    [Fact]
    public async Task OnPostAsyncProvisionsThePrinterAndShowsTheSnippetOnce()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (AddModel model, HSUser user) = await NewModelAsync(context);
        model.Input.Name = "Bench printer";
        model.Input.Location = "Workshop";

        // Act
        IActionResult result = await model.OnPostAsync(CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();

        Printer printer = await context.Printers.SingleAsync(TestContext.Current.CancellationToken);
        printer.Name.Should().Be("Bench printer");
        printer.Location.Should().Be("Workshop");

        model.Offer.Should().NotBeNull();
        model.Offer!.CanBuild.Should().BeTrue("the certificate covers the configured address");
        model.Offer.Snippet.Should().Contain("[service::connect]")
             .And.Contain("hostname = printers.example.com")
             .And.Contain("port = 443")
             .And.Contain("tls = True");

        PrusaConnectProvisioning stored =
            await context.PrusaConnectProvisionings.SingleAsync(TestContext.Current.CancellationToken);
        string tokenInSnippet = model.Offer!.Snippet.Split("token = ")[1].Trim();
        new TokenService().VerifyToken(tokenInSnippet, stored.HashedToken).Should()
                          .BeTrue("the snippet must carry the same token that was hashed and stored");

        model.Input.Name.Should().BeNull("the form resets after a successful provision, same as invite creation");
    }

    /// <summary>A team the caller cannot manage is rejected, and nothing is created.</summary>
    [Fact]
    public async Task OnPostAsyncRejectsATeamTheCallerCannotManage()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        (AddModel model, HSUser user) = await NewModelAsync(context);

        Team someoneElses = new() { CreatedBy = 999, CreatedAt = DateTimeOffset.UtcNow };
        context.Teams.Add(someoneElses);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        model.Input.TeamId = someoneElses.Id;

        // Act
        await model.OnPostAsync(CancellationToken.None);

        // Assert
        model.Offer.Should().BeNull();
        model.ModelState.IsValid.Should().BeFalse();
        (await context.Printers.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }
}
