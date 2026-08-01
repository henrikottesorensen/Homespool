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
using NSubstitute;

using Homespool.Data;
using Homespool.Host.Pages.Printers;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test.Printers;

/// <summary>
/// The printer detail page: the same "doesn't exist or can't read it" 404 rule
/// <see cref="PrinterQueryService.GetPrinterForUserAsync"/> follows, and that it correctly
/// surfaces live connection state.
/// </summary>
public sealed class DetailModelTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-printers-detail-{Guid.NewGuid():N}.db");

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

    private static async Task<(DetailModel model, HSUser user, Team team, PrinterConnectionRegistry connectionRegistry)> NewModelAsync(HSDbContext context)
    {
        (UserManager<HSUser> users, _, DefaultHttpContext httpContext, _) = IdentityTestHarness.BuildIdentityServices(context);

        HSUser user = new() { UserName = "owner@example.com", Email = "owner@example.com", EmailConfirmed = true };
        IdentityResult createResult = await users.CreateAsync(user, "Sup3rSecret!23");
        createResult.Succeeded.Should().BeTrue();

        Team team = context.AddDefaultTeam(user.Id, DateTimeOffset.UtcNow);
        await context.SaveChangesAsync();

        IdentityTestHarness.SignInAsPrincipal(httpContext, user);

        PrinterConnectionRegistry connectionRegistry = new(NullLogger<PrinterConnectionRegistry>.Instance);

        DetailModel model = new(new PrinterQueryService(context, TimeProvider.System), connectionRegistry, users)
        {
            PageContext = IdentityTestHarness.NewPageContext(httpContext),
        };

        return (model, user, team, connectionRegistry);
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

    [Fact]
    public async Task OnGetAsyncReturnsNotFoundForAnUnknownUuid()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (DetailModel model, _, _, _) = await NewModelAsync(context);

        // Act
        IActionResult result = await model.OnGetAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task OnGetAsyncReturnsNotFoundForAPrinterOnATeamTheCallerIsNotOn()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (DetailModel model, _, _, _) = await NewModelAsync(context);

        Team someoneElsesTeam = context.AddDefaultTeam(2, DateTimeOffset.UtcNow);
        await context.SaveChangesAsync();

        Printer printer = NewPrinter(someoneElsesTeam.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync();

        // Act
        IActionResult result = await model.OnGetAsync(printer.Uuid, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task OnGetAsyncPopulatesStatisticsForAnAccessiblePrinter()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (DetailModel model, _, Team team, _) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id, name: "MK3.5");
        context.Printers.Add(printer);
        await context.SaveChangesAsync();

        // Act
        IActionResult result = await model.OnGetAsync(printer.Uuid, CancellationToken.None);

        // Assert
        result.Should().BeOfType<PageResult>();
        model.Statistics.Printer.Id.Should().Be(printer.Id);
        model.Statistics.LiveState.Should().BeNull();
        model.Statistics.RecentSamples.Should().BeEmpty();
        model.Statistics.RecentEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task OnGetAsyncReflectsWhetherThePrinterIsCurrentlyConnected()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        (DetailModel model, _, Team team, PrinterConnectionRegistry connectionRegistry) = await NewModelAsync(context);

        Printer printer = NewPrinter(team.Id);
        context.Printers.Add(printer);
        await context.SaveChangesAsync();

        // Act - not connected
        await model.OnGetAsync(printer.Uuid, CancellationToken.None);

        // Assert
        model.Connected.Should().BeFalse();

        // Act - now connected
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        connectionRegistry.Register(printer.Id, actor);

        await model.OnGetAsync(printer.Uuid, CancellationToken.None);

        // Assert
        model.Connected.Should().BeTrue();
    }
}
