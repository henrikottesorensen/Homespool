using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Exceptions;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.DTO;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The registration code exchange: POST /p/register issues a code, the printer polls GET /p/register
/// until a user claims it, then receives its token.
/// </summary>
/// <remarks>
/// The pending state lives in <see cref="PrusaConnectRegistration"/>; the token issue materialises an
/// enrolled <see cref="PrusaConnectAuthenticationData"/> row and deletes the registration. Run against
/// real SQLite rather than the in-memory provider, because several of these depend on provider
/// behaviour — the timestamp comparison translating at all, and the deliberate absence of a unique
/// constraint on SerialNumber.
/// </remarks>
public sealed class PrinterRegistrationTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-reg-{Guid.NewGuid():N}.db");

    private static PrusaConnectService NewService(HomespoolDbContext context,
                                                  int lifetimeMinutes = 60,
                                                  ILogger<PrusaConnectService>? logger = null)
    {
        return new(context,
                   new CodeGenerator(),
                   new TokenService(),
                   new TeamService(context),
                   TimeProvider.System,
                   logger ?? NullLogger<PrusaConnectService>.Instance,
                   TestOptions.Monitor(new PrusaConnectOptions { RegistrationCodeLifetimeMinutes = lifetimeMinutes }));
    }

    private static RegisterPrinterRequestDTO Request(string serial = "15715-4842441651816441",
                                                     string fingerprint = "SUDBAJQ78CTJBNA8IHEMODUG43QD9H5GSBSFE0MMKBST8B9E0L",
                                                     string printerType = "1.3.5",
                                                     string firmware = "6.4.0+11974")
    {
        return new()
        {
            SerialNumber = serial,
            FingerPrint = fingerprint,
            PrinterType = printerType,
            Firmware = firmware,
        };
    }

    private HomespoolDbContext NewContext()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        return new HomespoolDbContext(options);
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

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        HomespoolDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

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
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        // Act
        CodeResponseDTO response = await NewService(context).GetPrinterCode(Request());

        // Assert
        PrusaConnectRegistration stored =
            await context.PrusaConnectRegistrations.SingleAsync(TestContext.Current.CancellationToken);

        stored.SerialNumber.Should().Be("15715-4842441651816441");
        stored.FingerPrint.Should().Be("SUDBAJQ78CTJBNA8IHEMODUG43QD9H5GSBSFE0MMKBST8B9E0L");
        stored.TemporaryCode.Should().Be(response.TemporaryCode);
        stored.PrinterId.Should().BeNull("nothing has claimed the printer yet");
    }

    /// <summary>
    /// Every registration gets a row and a code of its own, even for a fingerprint that already has
    /// one pending.
    /// </summary>
    /// <remarks>
    /// The POST is anonymous and names the printer by a fingerprint in its body, so a repeat that
    /// returned the pending code would read the printer's screen out to anyone holding the
    /// fingerprint. Firmware POSTs once per attempt and polls with the code it was given, so nothing
    /// depends on a repeat matching.
    /// </remarks>
    [Fact]
    public async Task ARepeatedRegistrationGetsACodeOfItsOwn()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        // Act
        string first = (await service.GetPrinterCode(Request())).TemporaryCode;
        string second = (await service.GetPrinterCode(Request())).TemporaryCode;

        // Assert
        second.Should().NotBe(first);
        (await context.PrusaConnectRegistrations.CountAsync(TestContext.Current.CancellationToken)).Should()
            .Be(2, "the two codes coexist");
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
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        // Act
        await NewService(context, lifetimeMinutes: 90).GetPrinterCode(Request());

        // Assert
        PrusaConnectRegistration stored =
            await context.PrusaConnectRegistrations.SingleAsync(TestContext.Current.CancellationToken);

        (stored.TemporaryCodeExpiry - stored.CreatedAt).Should()
                                                       .Be(TimeSpan.FromMinutes(90), "both come from a single clock read");
    }

    /// <summary>
    /// An expired code stays expired: the next registration gets a fresh row, and the expired one is
    /// cleared out rather than brought back.
    /// </summary>
    [Fact]
    public async Task AnExpiredCodeIsNotRenewedByTheNextRegistration()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string original = (await service.GetPrinterCode(Request())).TemporaryCode;
        long originalId = (await context.PrusaConnectRegistrations.SingleAsync(TestContext.Current.CancellationToken)).Id;
        await ExpireAsync(context, original);

        // Act
        string fresh = (await service.GetPrinterCode(Request())).TemporaryCode;

        // Assert
        fresh.Should().NotBe(original);

        await using HomespoolDbContext verify = NewContext();
        PrusaConnectRegistration stored =
            await verify.PrusaConnectRegistrations.SingleAsync(TestContext.Current.CancellationToken);
        stored.TemporaryCode.Should().Be(fresh, "the expired row was in hand and is gone");
        stored.Id.Should().NotBe(originalId, "a new registration, not the old one given a new code");

        Func<Task> poll = () => service.GetToken(original);
        await poll.Should().ThrowAsync<PrinterNotFoundException>("nothing brings an expired code back");
    }

    /// <summary>
    /// A claim made on a code that then expired is not inherited by the next registration.
    /// </summary>
    /// <remarks>
    /// Permission over the printer is checked when a code is claimed and at no other point. A fresh
    /// code that arrived already claimed would hand its token to the first poll, on the strength of a
    /// check made for a different code, possibly a long time ago.
    /// </remarks>
    [Fact]
    public async Task AClaimDoesNotSurviveItsCodeExpiring()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string original = (await service.GetPrinterCode(Request())).TemporaryCode;
        await ClaimAsync(context, original);
        await ExpireAsync(context, original);

        // Act
        string fresh = (await service.GetPrinterCode(Request())).TemporaryCode;

        // Assert
        (await service.GetToken(fresh)).Should().BeNull("nobody has claimed this code");
        (await context.PrusaConnectAuthentication.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
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
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await service.GetPrinterCode(Request(fingerprint: "FINGERPRINT-OF-ORIGINAL-BOARD"));

        // Act
        Func<Task> replacement = () => service.GetPrinterCode(Request(fingerprint: "FINGERPRINT-OF-NEW-BOARD"));

        // Assert
        await replacement.Should().NotThrowAsync();
        (await context.PrusaConnectRegistrations.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
    }

    // ---------- codes coexist: somebody else who knows the fingerprint ----------

    /// <summary>
    /// A stranger who POSTs a printer's fingerprint learns a code of their own, not the one on the
    /// printer's screen.
    /// </summary>
    /// <remarks>
    /// The screen code is what the owner will type, and the poll is keyed on a code alone - so the
    /// stranger holding it would only have to poll faster than the printer's five seconds to collect
    /// the token the owner's claim releases.
    /// </remarks>
    [Fact]
    public async Task AStrangersRegistrationGetsACodeDifferentFromThePrinters()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string printers = (await service.GetPrinterCode(Request())).TemporaryCode;

        // Act
        string strangers = (await service.GetPrinterCode(Request(serial: "NOT-THE-PRINTER"))).TemporaryCode;

        // Assert
        strangers.Should().NotBe(printers);
    }

    /// <summary>
    /// The owner's claim releases the token to the printer's code and to no other: the stranger's poll
    /// is still told to wait.
    /// </summary>
    [Fact]
    public async Task ClaimingThePrintersCodeGivesTheStrangersPollNothing()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string printers = (await service.GetPrinterCode(Request())).TemporaryCode;
        string strangers = (await service.GetPrinterCode(Request())).TemporaryCode;

        await ClaimAsync(context, printers);

        // Act
        // The stranger polls first, which is the race they would have to win.
        string? strangersToken = await service.GetToken(strangers);
        string? printersToken = await service.GetToken(printers);

        // Assert
        strangersToken.Should().BeNull("nobody claimed the stranger's code");
        printersToken.Should().NotBeNullOrWhiteSpace("the claim was of the printer's code");
    }

    /// <summary>
    /// A stranger's POST changes nothing about the registration the printer already holds - not its
    /// code, not its expiry, not a claim already made on it.
    /// </summary>
    [Fact]
    public async Task AStrangersRegistrationLeavesThePrintersRegistrationUntouched()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string printers = (await service.GetPrinterCode(Request())).TemporaryCode;
        Printer claimedAs = await ClaimAsync(context, printers);

        await using HomespoolDbContext before = NewContext();
        PrusaConnectRegistration original = await before.PrusaConnectRegistrations.SingleAsync(TestContext.Current.CancellationToken);

        // Act
        await service.GetPrinterCode(Request());

        // Assert
        await using HomespoolDbContext verify = NewContext();
        PrusaConnectRegistration after = await verify.PrusaConnectRegistrations.SingleAsync(
            registration => registration.Id == original.Id, TestContext.Current.CancellationToken);

        after.TemporaryCode.Should().Be(printers);
        after.TemporaryCodeExpiry.Should().Be(original.TemporaryCodeExpiry);
        after.PrinterId.Should().Be(claimedAs.Id);
    }

    /// <summary>
    /// One fingerprint never holds more than the cap, however many times it is POSTed: the oldest
    /// unclaimed registration makes way for the newest.
    /// </summary>
    /// <remarks>
    /// The newest is the one that has to survive. It is the only code a real printer is still polling -
    /// firmware abandons the previous code when registration is restarted - and a cap that refused
    /// instead would let a few POSTs made in advance keep the printer from registering at all.
    /// </remarks>
    [Fact]
    public async Task TheCapHoldsByDroppingTheOldestUnclaimedRegistration()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        List<string> codes = [];

        // Act
        for (int attempt = 0; attempt < PrusaConnectService.MaxPendingRegistrationsPerFingerprint + 3; attempt++)
        {
            codes.Add((await service.GetPrinterCode(Request())).TemporaryCode);
        }

        // Assert
        await using HomespoolDbContext verify = NewContext();
        List<string> held = await verify.PrusaConnectRegistrations
                                        .Select(registration => registration.TemporaryCode)
                                        .ToListAsync(TestContext.Current.CancellationToken);

        held.Should().BeEquivalentTo(codes.TakeLast(PrusaConnectService.MaxPendingRegistrationsPerFingerprint),
                                     "the newest registrations are the ones kept");
    }

    /// <summary>
    /// The cap is per fingerprint: another printer registering is not counted against this one, and is
    /// not what gets dropped.
    /// </summary>
    [Fact]
    public async Task TheCapDoesNotReachAcrossFingerprints()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string neighbours = (await service.GetPrinterCode(Request(fingerprint: "FINGERPRINT-OF-ANOTHER-PRINTER"))).TemporaryCode;

        // Act
        for (int attempt = 0; attempt < PrusaConnectService.MaxPendingRegistrationsPerFingerprint + 1; attempt++)
        {
            await service.GetPrinterCode(Request());
        }

        // Assert
        (await service.GetToken(neighbours)).Should().BeNull("the neighbour's registration is still pending");
        (await context.PrusaConnectRegistrations.CountAsync(TestContext.Current.CancellationToken)).Should()
            .Be(PrusaConnectService.MaxPendingRegistrationsPerFingerprint + 1);
    }

    /// <summary>
    /// A claimed registration is never the one dropped, however old it is and however many POSTs
    /// follow it.
    /// </summary>
    /// <remarks>
    /// Its owner has typed the code and the printer is a poll away from its token. If a POST could
    /// remove it, anyone holding the fingerprint could knock over a registration in flight.
    /// </remarks>
    [Fact]
    public async Task AClaimedRegistrationIsNeverDroppedToMakeRoom()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string printers = (await service.GetPrinterCode(Request())).TemporaryCode;
        await ClaimAsync(context, printers);

        // Act
        for (int attempt = 0; attempt < PrusaConnectService.MaxPendingRegistrationsPerFingerprint + 3; attempt++)
        {
            await service.GetPrinterCode(Request());
        }

        // Assert
        (await context.PrusaConnectRegistrations.CountAsync(TestContext.Current.CancellationToken)).Should()
            .Be(PrusaConnectService.MaxPendingRegistrationsPerFingerprint, "the cap still holds around it");
        (await service.GetToken(printers)).Should().NotBeNullOrWhiteSpace("the oldest row of all was the claimed one, and it is still there");
    }

    /// <summary>
    /// With every registration at the cap claimed there is nothing that may be dropped, so the POST is
    /// refused and the claimed registrations stay as they were.
    /// </summary>
    [Fact]
    public async Task ARegistrationIsRefusedWhenEveryPendingOneIsClaimed()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        for (int attempt = 0; attempt < PrusaConnectService.MaxPendingRegistrationsPerFingerprint; attempt++)
        {
            await service.GetPrinterCode(Request());
        }

        await ClaimAllAsync(context);

        // Act
        Func<Task> act = () => service.GetPrinterCode(Request());

        // Assert
        await act.Should().ThrowAsync<RegistrationLimitReachedException>();

        await using HomespoolDbContext verify = NewContext();
        List<PrusaConnectRegistration> held = await verify.PrusaConnectRegistrations.ToListAsync(TestContext.Current.CancellationToken);

        held.Should().HaveCount(PrusaConnectService.MaxPendingRegistrationsPerFingerprint);
        held.Should().OnlyContain(registration => registration.PrinterId != null);
    }

    /// <summary>
    /// An expired registration does not count towards the cap, claimed or not - it is refused by every
    /// lookup already, so it cannot be what stands between a printer and a code.
    /// </summary>
    [Fact]
    public async Task ExpiredRegistrationsDoNotCountTowardsTheCap()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        for (int attempt = 0; attempt < PrusaConnectService.MaxPendingRegistrationsPerFingerprint; attempt++)
        {
            string code = (await service.GetPrinterCode(Request())).TemporaryCode;
            await ClaimAsync(context, code);
            await ExpireAsync(context, code);
        }

        // Act
        string fresh = (await service.GetPrinterCode(Request())).TemporaryCode;

        // Assert
        (await service.GetToken(fresh)).Should().BeNull("the registration was accepted and is waiting for a claim");
    }

    /// <summary>
    /// Collecting the token ends every other registration pending for that fingerprint, claimed ones
    /// included, and nobody else's.
    /// </summary>
    /// <remarks>
    /// The printer has its token, so no printer is waiting on any of them. A claimed one left behind
    /// is the dangerous kind: redeemed later, it would rotate the credential out from under the
    /// printer that has just enrolled.
    /// </remarks>
    [Fact]
    public async Task CollectingTheTokenRemovesTheFingerprintsOtherRegistrations()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string abandoned = (await service.GetPrinterCode(Request())).TemporaryCode;
        string claimedAndAbandoned = (await service.GetPrinterCode(Request())).TemporaryCode;
        string printers = (await service.GetPrinterCode(Request())).TemporaryCode;
        string neighbours = (await service.GetPrinterCode(Request(fingerprint: "FINGERPRINT-OF-ANOTHER-PRINTER"))).TemporaryCode;

        await ClaimAsync(context, claimedAndAbandoned);
        await ClaimAsync(context, printers);

        // Act
        (await service.GetToken(printers)).Should().NotBeNullOrWhiteSpace();

        // Assert
        await using HomespoolDbContext verify = NewContext();
        List<string> held = await verify.PrusaConnectRegistrations
                                        .Select(registration => registration.TemporaryCode)
                                        .ToListAsync(TestContext.Current.CancellationToken);

        held.Should().BeEquivalentTo([neighbours], "only the other printer's registration is left");
        held.Should().NotContain([abandoned, claimedAndAbandoned]);
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
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string code = (await service.GetPrinterCode(Request())).TemporaryCode;

        // Assert
        (await service.GetToken(code)).Should().BeNull("the controller turns this into a 202");
    }

    /// <summary>
    /// Once claimed, polling issues a token, materialises the enrolled credential, and stores only the
    /// token's hash.
    /// </summary>
    /// <remarks>
    /// The token is a long-lived credential authenticating every subsequent request, so a database
    /// copy would be worth stealing. Asserted by checking the stored value differs from the token and
    /// still verifies against it.
    /// </remarks>
    [Fact]
    public async Task PollingAClaimedRegistrationIssuesATokenAndStoresOnlyItsHash()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string code = (await service.GetPrinterCode(Request())).TemporaryCode;
        await ClaimAsync(context);

        // Act
        string? token = await service.GetToken(code);

        // Assert
        token.Should().NotBeNullOrWhiteSpace();

        PrusaConnectAuthenticationData enrolled =
            await context.PrusaConnectAuthentication.SingleAsync(TestContext.Current.CancellationToken);
        enrolled.HashedToken.Should().NotBeNullOrWhiteSpace();
        enrolled.HashedToken.Should().NotBe(token, "the token must never be stored in the clear");
        new TokenService().VerifyToken(token, enrolled.HashedToken).Should().BeTrue();

        (await context.PrusaConnectRegistrations.AnyAsync(TestContext.Current.CancellationToken)).Should()
            .BeFalse("the registration is consumed once the token is issued");
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
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();

        // Act
        Func<Task> act = () => NewService(context).GetToken("NEVER-ISSUED");

        // Assert
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
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string code = (await service.GetPrinterCode(Request())).TemporaryCode;
        await ClaimAsync(context);

        PrusaConnectRegistration stored =
            await context.PrusaConnectRegistrations.SingleAsync(TestContext.Current.CancellationToken);
        stored.TemporaryCodeExpiry = DateTimeOffset.UtcNow.AddSeconds(-1);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        Func<Task> act = () => service.GetToken(code);

        // Assert
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
        // Arrange
        // Exception messages reach logs, and the code is a credential: whoever holds it can claim the
        // printer.
        await using HomespoolDbContext context = await MigratedContextAsync();

        // Act
        Exception? thrown = await Record.ExceptionAsync(() => NewService(context).GetToken("SECRET-CODE-VALUE"));

        // Assert
        thrown.Should().BeOfType<PrinterNotFoundException>();
        thrown.Message.Should().NotContain("SECRET-CODE-VALUE");
    }

    /// <summary>
    /// A code stops working the moment it has been redeemed for a token.
    /// </summary>
    /// <remarks>
    /// It used to stay live for the rest of its lifetime - up to <c>RegistrationCodeLifetimeMinutes</c>,
    /// an hour by default - so anyone else holding it could redeem it again. Now the registration is
    /// deleted on redemption, so a replay finds nothing.
    /// </remarks>
    [Fact]
    public async Task ARedeemedCodeCannotBeRedeemedAgain()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string code = (await service.GetPrinterCode(Request())).TemporaryCode;
        await ClaimAsync(context);

        (await service.GetToken(code)).Should().NotBeNullOrWhiteSpace();

        // Act
        Func<Task> replay = () => service.GetToken(code);

        // Assert
        await replay.Should().ThrowAsync<PrinterNotFoundException>("a consumed code is indistinguishable from an unknown one");
    }

    /// <summary>
    /// A replay does not overwrite the hash, so the printer that registered keeps working.
    /// </summary>
    /// <remarks>
    /// The half of this that bites hardest. Re-redeeming used to mint a fresh token and overwrite
    /// <c>HashedToken</c> with its hash, a denial of service against the real printer. Now the
    /// registration is gone after the first redemption, so the replay throws before touching the
    /// enrolled credential.
    /// </remarks>
    [Fact]
    public async Task AReplayedCodeDoesNotInvalidateTheTokenAlreadyIssued()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string code = (await service.GetPrinterCode(Request())).TemporaryCode;
        await ClaimAsync(context);

        string token = (await service.GetToken(code))!;

        // Act
        await Record.ExceptionAsync(() => service.GetToken(code));

        // Assert
        PrusaConnectAuthenticationData enrolled =
            await context.PrusaConnectAuthentication.SingleAsync(TestContext.Current.CancellationToken);

        new TokenService().VerifyToken(token, enrolled.HashedToken).Should()
                          .BeTrue("the printer's token must survive someone else replaying the code");
    }

    /// <summary>
    /// Polling while unclaimed does not consume the code.
    /// </summary>
    /// <remarks>
    /// The constraint that rules out the obvious implementation. Buddy polls this endpoint on a loop
    /// for as long as it takes a user to claim the printer, so consuming on first contact - rather
    /// than on redemption - would kill registration on poll two and leave the printer retrying a code
    /// the server had already thrown away.
    /// </remarks>
    [Fact]
    public async Task PollingRepeatedlyBeforeBeingClaimedDoesNotConsumeTheCode()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string code = (await service.GetPrinterCode(Request())).TemporaryCode;

        // Act
        for (int poll = 0; poll < 5; poll++)
        {
            (await service.GetToken(code)).Should().BeNull("nobody has claimed the printer yet");
        }

        await ClaimAsync(context);

        // Assert
        (await service.GetToken(code)).Should().NotBeNullOrWhiteSpace("the code survived the polling loop");
    }

    /// <summary>
    /// Issuing a token stamps when enrolment completed.
    /// </summary>
    /// <remarks>
    /// The enrolled credential's <c>EnrolledAt</c> is set at the one moment it can mean anything -
    /// redemption, when the token is issued and the row is materialised.
    /// </remarks>
    [Fact]
    public async Task IssuingATokenRecordsWhenEnrolmentCompleted()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string code = (await service.GetPrinterCode(Request())).TemporaryCode;
        await ClaimAsync(context);

        // Act
        DateTimeOffset before = DateTimeOffset.UtcNow;
        await service.GetToken(code);

        // Assert
        PrusaConnectAuthenticationData enrolled =
            await context.PrusaConnectAuthentication.SingleAsync(TestContext.Current.CancellationToken);

        enrolled.EnrolledAt.Should().BeOnOrAfter(before.AddSeconds(-1)).And.BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(1));
    }

    // ---------- what reaches the log ----------

    /// <summary>
    /// Issuing a code does not write the code, or the fingerprint, to the log.
    /// </summary>
    /// <remarks>
    /// The same reasoning as
    /// <see cref="TheExceptionForAnUnknownCodeDoesNotLeakTheCode"/>, which the logging used to
    /// contradict: both issue and renewal wrote the code at Information level, along with a
    /// destructured request DTO carrying the fingerprint. Serilog's minimum level is Debug and the
    /// sink is the console, so in the container that is stdout and every code went wherever those
    /// logs are shipped. <see cref="PrusaConnectService.GetToken"/> looks a printer up by code and by
    /// nothing else, so a reader of the logs could claim any printer registered in the last hour.
    /// </remarks>
    [Fact]
    public async Task IssuingACodeDoesNotWriteTheCodeOrFingerprintToTheLog()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        using CapturingSink sink = new();

        // Act
        string code = (await NewService(context, logger: sink.AsLogger<PrusaConnectService>()).GetPrinterCode(Request()))
            .TemporaryCode;

        // Assert
        sink.Entries.Should().NotBeEmpty("issuing a code is still worth an operational record");
        sink.Entries.Should().NotContainMatch($"*{code}*");
        sink.Entries.Should().NotContainMatch("*SUDBAJQ78CTJBNA8IHEMODUG43QD9H5GSBSFE0MMKBST8B9E0L*");
    }

    /// <summary>
    /// A registration that had to make room does not write its code to the log either.
    /// </summary>
    /// <remarks>
    /// Making room logs what it dropped, separately from the issue, so it needs its own guard: the
    /// dropped registrations are named by id, never by the codes they carried.
    /// </remarks>
    [Fact]
    public async Task MakingRoomDoesNotWriteAnyCodeToTheLog()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        using CapturingSink sink = new();
        PrusaConnectService service = NewService(context, logger: sink.AsLogger<PrusaConnectService>());

        List<string> codes = [];

        // Act
        for (int attempt = 0; attempt <= PrusaConnectService.MaxPendingRegistrationsPerFingerprint; attempt++)
        {
            codes.Add((await service.GetPrinterCode(Request())).TemporaryCode);
        }

        // Assert
        sink.Entries.Should().ContainMatch("*dropped the oldest unclaimed*", "the last registration had to make room");

        foreach (string code in codes)
        {
            sink.Entries.Should().NotContainMatch($"*{code}*");
        }
    }

    /// <summary>
    /// The registration's row id is logged, so an issue can still be correlated with the later poll.
    /// </summary>
    /// <remarks>
    /// The point of removing the code was not to stop logging. Without a stable identifier the
    /// records would be untraceable, and the temptation would be to put the code back.
    /// </remarks>
    [Fact]
    public async Task IssuingACodeLogsTheRegistrationIdForCorrelation()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        using CapturingSink sink = new();

        // Act
        await NewService(context, logger: sink.AsLogger<PrusaConnectService>()).GetPrinterCode(Request());

        // Assert
        PrusaConnectRegistration stored =
            await context.PrusaConnectRegistrations.SingleAsync(TestContext.Current.CancellationToken);

        stored.Id.Should().BeGreaterThan(0, "the key is assigned by the insert, so the log has to come after the save");
        sink.Entries.Should().ContainMatch($"RegistrationId={stored.Id}");
    }

    /// <summary>
    /// What the printer said about itself reaches the log with its control characters replaced, from
    /// all three places that log it: the issue, making room, and the refusal.
    /// </summary>
    /// <remarks>
    /// The endpoint is anonymous, so the serial, the model and the firmware version are three strings
    /// a stranger chose and length is the only other rule they pass; an escape sequence among them
    /// repaints the terminal of whoever reads the log. <c>LogTextTests</c> pins what cleaning means -
    /// this pins that these log sites do it, which is the half that deleting the calls would leave
    /// green.
    /// </remarks>
    [Fact]
    public async Task RegistrationFieldsReachTheLogWithoutControlCharacters()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        using CapturingSink sink = new();
        PrusaConnectService service = NewService(context, logger: sink.AsLogger<PrusaConnectService>());

        RegisterPrinterRequestDTO printer = Request(serial: "15715\u001B[2J",
                                                    printerType: "1.3.5\n1.3.6",
                                                    firmware: "6.4.0\u0000");

        // Act
        // One past the cap, so the last of these makes room and says so.
        for (int attempt = 0; attempt <= PrusaConnectService.MaxPendingRegistrationsPerFingerprint; attempt++)
        {
            await service.GetPrinterCode(printer);
        }

        // Every pending registration claimed, so the next is refused and says so.
        await ClaimAllAsync(context);
        await Record.ExceptionAsync(() => service.GetPrinterCode(printer));

        // Assert
        sink.Entries.Should().ContainMatch("*dropped the oldest unclaimed*");
        sink.Entries.Should().ContainMatch("*was refused a Connect code*");
        sink.Entries.Should().AllSatisfy(
            entry => entry.Should().NotContainAny("\u001B", "\n", "\r", "\u0000"));
        sink.Entries.Should().ContainMatch("*15715\uFFFD*",
                                           "a cleaned value still says what arrived; a dropped one reads as a serial nobody sent");
    }

    /// <summary>
    /// Captures what a sink would receive, through a real Serilog pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not a bare <see cref="ILogger{T}"/> stub. The first version of this was, and it
    /// made the fingerprint half of these assertions vacuous: <c>Microsoft.Extensions.Logging</c>'s
    /// default formatter calls <see cref="object.ToString"/> on the argument, so <c>{@Printer}</c>
    /// captured the literal string <c>Homespool.Host.PrusaConnect.DTO.RegisterPrinterRequestDTO</c>
    /// and the assertion passed against the very code it was written to catch. Destructuring is a
    /// Serilog feature and only happens in Serilog's pipeline - which is what runs in production.
    /// </para>
    /// <para>
    /// Both the rendered message and the properties are flattened, because
    /// <c>RenderedCompactJsonFormatter</c> writes both and a value can reach the sink as a property
    /// without appearing in the text.
    /// </para>
    /// </remarks>
    private sealed class CapturingSink : ILogEventSink, IDisposable
    {
        private readonly List<LogEvent> _events = [];
        private SerilogLoggerFactory? _factory;

        public IEnumerable<string> Entries => _events.SelectMany(Flatten);

        public void Emit(LogEvent logEvent)
        {
            _events.Add(logEvent);
        }

        public ILogger<T> AsLogger<T>()
        {
            // Owned rather than left to the finalizer so the pipeline is torn down with the test.
            _factory ??= new SerilogLoggerFactory(
                new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(this).CreateLogger());

            return _factory.CreateLogger<T>();
        }

        public void Dispose()
        {
            _factory?.Dispose();
        }

        private static IEnumerable<string> Flatten(LogEvent logEvent)
        {
            yield return logEvent.RenderMessage();

            foreach (KeyValuePair<string, LogEventPropertyValue> property in logEvent.Properties)
            {
                // ToString on a StructureValue renders its nested members, so a destructured object
                // is covered without walking the tree by hand.
                yield return $"{property.Key}={property.Value}";
            }
        }
    }

    private static async Task ExpireAsync(HomespoolDbContext context, string code)
    {
        PrusaConnectRegistration stored = await context.PrusaConnectRegistrations.SingleAsync(
            registration => registration.TemporaryCode == code, TestContext.Current.CancellationToken);

        stored.TemporaryCodeExpiry = DateTimeOffset.UtcNow.AddHours(-1);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task ClaimAllAsync(HomespoolDbContext context)
    {
        List<string> codes = await context.PrusaConnectRegistrations
                                          .Where(registration => registration.PrinterId == null)
                                          .Select(registration => registration.TemporaryCode)
                                          .ToListAsync(TestContext.Current.CancellationToken);

        foreach (string code in codes)
        {
            await ClaimAsync(context, code);
        }
    }

    /// <summary>
    /// Claims a registration the way a user would, as a printer of its own in a team of its own. With
    /// no <paramref name="code"/>, claims the only registration there is.
    /// </summary>
    private static async Task<Printer> ClaimAsync(HomespoolDbContext context, string? code = null)
    {
        // A printer belongs to a team, and foreign keys are enforced, so the owning team has to
        // exist before the printer can reference it.
        Team team = new()
        {
            CreatedBy = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            Type = default,
            TeamId = team.Id,
            Status = default,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        PrusaConnectRegistration registration = await context.PrusaConnectRegistrations.SingleAsync(
            pending => code == null || pending.TemporaryCode == code, TestContext.Current.CancellationToken);
        registration.PrinterId = printer.Id;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return printer;
    }
}
