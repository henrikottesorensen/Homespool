using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using PrinterService.Api.Exceptions;
using PrinterService.Api.PrusaConnect;
using PrinterService.Api.PrusaConnect.DTO;
using PrinterService.Data;
using PrinterService.Model.Entities;

namespace PrinterService.Api.Test;

/// <summary>
/// The registration code exchange: POST /p/register issues a code, the printer polls GET /p/register
/// until a user claims it, then receives its token.
/// </summary>
/// <remarks>
/// Run against real SQLite rather than the in-memory provider, because several of these depend on
/// provider behaviour — the timestamp comparison translating at all, and the deliberate absence of a
/// unique constraint on SerialNumber.
/// </remarks>
public sealed class PrinterRegistrationTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-reg-{Guid.NewGuid():N}.db");

    private PSDbContext NewContext()
    {
        DbContextOptions<PSDbContext> options = new DbContextOptionsBuilder<PSDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        return new PSDbContext(options);
    }

    private static PrusaConnectService NewService(PSDbContext context, int lifetimeMinutes = 60) =>
        new(context,
            new CodeGenerator(),
            new TokenService(),
            NullLogger<PrusaConnectService>.Instance,
            Options.Create(new PrusaConnectOptions { RegistrationCodeLifetimeMinutes = lifetimeMinutes }));

    private static RegisterPrinterRequestDTO Request(string serial = "15715-4842441651816441",
                                                     string fingerprint = "SUDBAJQ78CTJBNA8IHEMODUG43QD9H5GSBSFE0MMKBST8B9E0L") => new()
    {
        SerialNumber = serial,
        FingerPrint = fingerprint,
        PrinterType = "1.3.5",
        Firmware = "6.4.0+11974",
    };

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

    private async Task<PSDbContext> MigratedContextAsync()
    {
        PSDbContext context = NewContext();
        await context.Database.MigrateAsync();

        return context;
    }

    // ---------- POST /p/register ----------

    /// <summary>
    /// A first-time registration stores the printer's identity and the code it was handed.
    /// </summary>
    /// <remarks>
    /// <c>PrinterId</c> stays null: registering and being claimed are separate steps, and the printer
    /// polls until a user completes the second.
    /// </remarks>
    [Fact]
    public async Task FirstRegistrationPersistsTheSerialFingerprintAndCode()
    {
        await using PSDbContext context = await MigratedContextAsync();

        CodeResponseDTO response = await NewService(context).GetPrinterCode(Request());

        PrusaConnectAuthenticationData stored = await context.PrusaConnectAuthentication.SingleAsync();

        stored.SerialNumber.Should().Be("15715-4842441651816441");
        stored.FingerPrint.Should().Be("SUDBAJQ78CTJBNA8IHEMODUG43QD9H5GSBSFE0MMKBST8B9E0L");
        stored.TemporaryCode.Should().Be(response.TemporaryCode);
        stored.PrinterId.Should().BeNull("nothing has claimed the printer yet");
    }

    /// <summary>
    /// Re-registering returns the existing code rather than issuing a new one.
    /// </summary>
    /// <remarks>
    /// The printer re-POSTs on every reconnect. If each attempt minted a fresh code, a user reading
    /// one off the printer's screen would be chasing a value that had already changed.
    /// </remarks>
    [Fact]
    public async Task RepeatedRegistrationReturnsTheSameCodeWhileItIsStillValid()
    {
        // The printer re-POSTs on every reconnect. It must not get a fresh code each time, or a user
        // reading one off the screen would be chasing a moving target.
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string first = (await service.GetPrinterCode(Request())).TemporaryCode;
        string second = (await service.GetPrinterCode(Request())).TemporaryCode;

        second.Should().Be(first);
        (await context.PrusaConnectAuthentication.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// Expiry is exactly <c>RegistrationCodeLifetimeMinutes</c> after creation, to the millisecond.
    /// </summary>
    /// <remarks>
    /// Exact rather than approximate because both values come from a single clock read. Two reads
    /// used to drift by however long the intervening work took - measured at 55 ms - which would make
    /// this assertion impossible to state precisely.
    /// </remarks>
    [Fact]
    public async Task ExpiryIsExactlyTheConfiguredLifetimeAfterCreation()
    {
        await using PSDbContext context = await MigratedContextAsync();

        await NewService(context, lifetimeMinutes: 90).GetPrinterCode(Request());

        PrusaConnectAuthenticationData stored = await context.PrusaConnectAuthentication.SingleAsync();

        (stored.TemporaryCodeExpiry - stored.CreatedAt).Should()
            .Be(TimeSpan.FromMinutes(90), "both come from a single clock read");
    }

    /// <summary>
    /// An expired code is renewed in place on the next registration, not duplicated.
    /// </summary>
    /// <remarks>
    /// This is why an expired code is a delay rather than a dead end. It matters that the row is
    /// reused: a second row for the same fingerprint would violate its unique index.
    /// </remarks>
    [Fact]
    public async Task AnExpiredCodeIsReplacedOnTheNextRegistration()
    {
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string original = (await service.GetPrinterCode(Request())).TemporaryCode;

        PrusaConnectAuthenticationData stored = await context.PrusaConnectAuthentication.SingleAsync();
        stored.TemporaryCodeExpiry = DateTimeOffset.UtcNow.AddHours(-1);
        await context.SaveChangesAsync();

        string renewed = (await service.GetPrinterCode(Request())).TemporaryCode;

        renewed.Should().NotBe(original);
        (await context.PrusaConnectAuthentication.CountAsync()).Should().Be(1, "the row is renewed, not duplicated");
    }

    /// <summary>
    /// A mainboard replacement: service re-burns the original serial onto a new board, so the CPU UUID
    /// and MAC change and the fingerprint changes with them.
    /// </summary>
    /// <remarks>
    /// This used to be a 500 — <c>UNIQUE constraint failed: SerialNumber</c> — and the firmware gives
    /// the initial POST only three attempts before abandoning registration permanently. Guards the
    /// deliberate absence of that unique index.
    /// </remarks>
    [Fact]
    public async Task AReplacementMainboardWithTheSameSerialCanStillRegister()
    {
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await service.GetPrinterCode(Request(fingerprint: "FINGERPRINT-OF-ORIGINAL-BOARD"));

        Func<Task> replacement = () => service.GetPrinterCode(Request(fingerprint: "FINGERPRINT-OF-NEW-BOARD"));

        await replacement.Should().NotThrowAsync();
        (await context.PrusaConnectAuthentication.CountAsync()).Should().Be(2);
    }

    // ---------- GET /p/register ----------

    /// <summary>
    /// Polling before anyone has claimed the printer yields no token.
    /// </summary>
    /// <remarks>
    /// The controller turns null into <c>202 Accepted</c>, which is what tells Buddy to keep polling.
    /// Returning anything else would end registration - the firmware treats any unexpected status as
    /// a server error.
    /// </remarks>
    [Fact]
    public async Task PollingAnUnclaimedRegistrationReturnsNoToken()
    {
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string code = (await service.GetPrinterCode(Request())).TemporaryCode;

        (await service.GetToken(code)).Should().BeNull("the controller turns this into a 202");
    }

    /// <summary>
    /// Once claimed, polling issues a token - and only its hash is persisted.
    /// </summary>
    /// <remarks>
    /// The token is a long-lived credential authenticating every subsequent request, so a database
    /// copy would be worth stealing. Asserted by checking the stored value differs from the token and
    /// still verifies against it.
    /// </remarks>
    [Fact]
    public async Task PollingAClaimedRegistrationIssuesATokenAndStoresOnlyItsHash()
    {
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string code = (await service.GetPrinterCode(Request())).TemporaryCode;
        await ClaimAsync(context);

        string? token = await service.GetToken(code);

        token.Should().NotBeNullOrWhiteSpace();

        PrusaConnectAuthenticationData stored = await context.PrusaConnectAuthentication.SingleAsync();
        stored.HashedToken.Should().NotBeNullOrWhiteSpace();
        stored.HashedToken.Should().NotBe(token, "the token must never be stored in the clear");
        new TokenService().VerifyToken(token, stored.HashedToken!).Should().BeTrue();
    }

    /// <summary>
    /// A code that was never issued is rejected.
    /// </summary>
    /// <remarks>
    /// Surfaces as 404. Since the code is now the sole lookup key for the poll, this is the boundary
    /// that stops an invented code being redeemed.
    /// </remarks>
    [Fact]
    public async Task PollingWithAnUnknownCodeIsRejected()
    {
        await using PSDbContext context = await MigratedContextAsync();

        Func<Task> act = () => NewService(context).GetToken("NEVER-ISSUED");

        await act.Should().ThrowAsync<PrinterNotFoundException>();
    }

    /// <summary>
    /// An expired code is indistinguishable from an unknown one.
    /// </summary>
    /// <remarks>
    /// Also the regression guard for the query itself: this predicate compares timestamps in SQL,
    /// which only translates because they are stored as epoch milliseconds. Against EF's default
    /// DateTimeOffset mapping it throws, and the controller turns that into a 400 on every poll.
    /// </remarks>
    [Fact]
    public async Task PollingWithAnExpiredCodeIsRejectedLikeAnUnknownOne()
    {
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string code = (await service.GetPrinterCode(Request())).TemporaryCode;
        await ClaimAsync(context);

        PrusaConnectAuthenticationData stored = await context.PrusaConnectAuthentication.SingleAsync();
        stored.TemporaryCodeExpiry = DateTimeOffset.UtcNow.AddSeconds(-1);
        await context.SaveChangesAsync();

        Func<Task> act = () => service.GetToken(code);

        await act.Should().ThrowAsync<PrinterNotFoundException>();
    }

    /// <summary>
    /// The rejection message does not contain the code that was offered.
    /// </summary>
    /// <remarks>
    /// Exception messages reach logs, and whoever holds a valid code can claim the printer. The
    /// exception type's other constructors format a fingerprint into the message, so it would have
    /// been easy to pass the code the same way.
    /// </remarks>
    [Fact]
    public async Task TheExceptionForAnUnknownCodeDoesNotLeakTheCode()
    {
        // Exception messages reach logs, and the code is a credential: whoever holds it can claim the
        // printer.
        await using PSDbContext context = await MigratedContextAsync();

        Exception thrown = await Record.ExceptionAsync(() => NewService(context).GetToken("SECRET-CODE-VALUE"));

        thrown.Should().BeOfType<PrinterNotFoundException>();
        thrown.Message.Should().NotContain("SECRET-CODE-VALUE");
    }

    private static async Task ClaimAsync(PSDbContext context)
    {
        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            Type = default,
            Owner = 1,
            Status = default,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync();

        PrusaConnectAuthenticationData auth = await context.PrusaConnectAuthentication.SingleAsync();
        auth.PrinterId = printer.Id;
        await context.SaveChangesAsync();
    }
}
