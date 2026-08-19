using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Data;
using Homespool.Host.PrusaConnect.DTO;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The two bounds on <c>POST /p/register</c>: how large a row can be, and how long one lives.
/// </summary>
/// <remarks>
/// That endpoint is anonymous, so both are the only things standing between a caller and an
/// unbounded table. The rate limiter counts requests, not rows or bytes.
/// </remarks>
public sealed class RegistrationRetentionTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-regret-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task AnExpiredRegistrationIsSweptAway()
    {
        await using (HomespoolDbContext context = await MigratedContextAsync())
        {
            await AddAsync(context, "EXPIRED001", DateTimeOffset.UtcNow.AddMinutes(-1));
            await AddAsync(context, "STILLGOOD1", DateTimeOffset.UtcNow.AddMinutes(30));
        }

        await SweepAsync();

        await using HomespoolDbContext after = NewContext();

        after.PrusaConnectRegistrations
             .Select(registration => registration.TemporaryCode)
             .Should().BeEquivalentTo(["STILLGOOD1"],
                                      "an expired code is refused by every lookup already, so keeping the row only "
                                      + "grows the table");
    }

    /// <summary>
    /// The sweep must not touch a claimed registration whose code is still live: the printer has not
    /// collected its token yet, and deleting the row would strand the enrolment.
    /// </summary>
    [Fact]
    public async Task AClaimedButUnexpiredRegistrationSurvives()
    {
        await using (HomespoolDbContext context = await MigratedContextAsync())
        {
            Team team = new() { Name = "Workshop", CreatedAt = DateTimeOffset.UtcNow };
            context.Teams.Add(team);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            Printer printer = new()
            {
                Uuid = Guid.NewGuid(),
                Type = Homespool.Model.PrinterType.PrusaConnect,
                TeamId = team.Id,
                Status = Homespool.Model.PrinterStatus.Unknown,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            context.Printers.Add(printer);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            PrusaConnectRegistration claimed = Build("CLAIMED001", DateTimeOffset.UtcNow.AddMinutes(30));
            claimed.PrinterId = printer.Id;

            context.PrusaConnectRegistrations.Add(claimed);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SweepAsync();

        await using HomespoolDbContext after = NewContext();

        (await after.PrusaConnectRegistrations.CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(1, "the printer has still to collect the token this claim produced");
    }

    /// <summary>
    /// The caps are what stop one anonymous request storing megabytes. They are validated by
    /// <c>[ApiController]</c> before the action runs, so the attributes are the whole enforcement.
    /// </summary>
    [Theory]
    [InlineData(nameof(RegisterPrinterRequestDTO.SerialNumber))]
    [InlineData(nameof(RegisterPrinterRequestDTO.FingerPrint))]
    [InlineData(nameof(RegisterPrinterRequestDTO.PrinterType))]
    [InlineData(nameof(RegisterPrinterRequestDTO.Firmware))]
    public void EveryFieldIsLengthCapped(string property)
    {
        typeof(RegisterPrinterRequestDTO)
            .GetProperty(property)!
            .GetCustomAttributes(typeof(StringLengthAttribute), inherit: false)
            .Should().NotBeEmpty($"{property} is stored verbatim from an anonymous request");
    }

    /// <summary>
    /// A real printer must fit comfortably: firmware reads any non-2xx from this endpoint as a server
    /// error and burns one of only three registration retries.
    /// </summary>
    [Fact]
    public void ARealPrinterFitsInsideTheCaps()
    {
        RegisterPrinterRequestDTO real = new()
        {
            SerialNumber = "29990-19546993452019360418",
            FingerPrint = new string('a', 50),
            PrinterType = "1.3.5",
            Firmware = "6.4.0+11974",
        };

        Validator.TryValidateObject(real, new ValidationContext(real), null, validateAllProperties: true)
                 .Should().BeTrue("the shapes firmware actually sends are well inside every cap");
    }

    [Fact]
    public void AnOversizedFieldIsRefused()
    {
        RegisterPrinterRequestDTO oversized = new()
        {
            SerialNumber = new string('x', RegisterPrinterRequestDTO.SerialNumberMaxLength + 1),
            FingerPrint = new string('a', 50),
            PrinterType = "1.3.5",
            Firmware = "6.4.0+11974",
        };

        Validator.TryValidateObject(oversized, new ValidationContext(oversized), null, validateAllProperties: true)
                 .Should().BeFalse();
    }

    private static PrusaConnectRegistration Build(string code, DateTimeOffset expiry)
    {
        return new PrusaConnectRegistration
        {
            SerialNumber = "TEST-0001",
            FingerPrint = code + "-fingerprint",
            TemporaryCode = code,
            TemporaryCodeExpiry = expiry,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static async Task AddAsync(HomespoolDbContext context, string code, DateTimeOffset expiry)
    {
        context.PrusaConnectRegistrations.Add(Build(code, expiry));

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SweepAsync()
    {
        ServiceCollection services = new();
        services.AddDbContext<HomespoolDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));

        await using ServiceProvider provider = services.BuildServiceProvider();

        using RegistrationRetentionService sweep = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RegistrationRetentionService>.Instance);

        // One pass, driven directly. Start/StopAsync would prove nothing: on .NET 10 StartAsync
        // schedules ExecuteAsync onto the pool and returns, so the stop can win the race and the
        // sweep never runs - which is exactly what this test saw before it called SweepAsync.
        await sweep.SweepAsync(CancellationToken.None);
    }

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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
