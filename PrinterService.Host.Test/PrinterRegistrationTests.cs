using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

using PrinterService.Host.Exceptions;
using PrinterService.Host.PrusaConnect;
using PrinterService.Host.PrusaConnect.DTO;
using PrinterService.Host.Services;
using PrinterService.Data;
using PrinterService.Model.Entities;

namespace PrinterService.Host.Test;

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

    private static PrusaConnectService NewService(PSDbContext context,
                                                  int lifetimeMinutes = 60,
                                                  ILogger<PrusaConnectService>? logger = null) =>
        new(context,
            new CodeGenerator(),
            new TokenService(),
            new TeamService(context),
            logger ?? NullLogger<PrusaConnectService>.Instance,
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
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        // Act
        CodeResponseDTO response = await NewService(context).GetPrinterCode(Request());

        // Assert
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
        // Arrange
        // The printer re-POSTs on every reconnect. It must not get a fresh code each time, or a user
        // reading one off the screen would be chasing a moving target.
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        // Act
        string first = (await service.GetPrinterCode(Request())).TemporaryCode;
        string second = (await service.GetPrinterCode(Request())).TemporaryCode;

        // Assert
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
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

        // Act
        await NewService(context, lifetimeMinutes: 90).GetPrinterCode(Request());

        // Assert
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
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string original = (await service.GetPrinterCode(Request())).TemporaryCode;

        PrusaConnectAuthenticationData stored = await context.PrusaConnectAuthentication.SingleAsync();
        stored.TemporaryCodeExpiry = DateTimeOffset.UtcNow.AddHours(-1);
        await context.SaveChangesAsync();

        // Act
        string renewed = (await service.GetPrinterCode(Request())).TemporaryCode;

        // Assert
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
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        await service.GetPrinterCode(Request(fingerprint: "FINGERPRINT-OF-ORIGINAL-BOARD"));

        // Act
        Func<Task> replacement = () => service.GetPrinterCode(Request(fingerprint: "FINGERPRINT-OF-NEW-BOARD"));

        // Assert
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
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string code = (await service.GetPrinterCode(Request())).TemporaryCode;

        // Assert
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
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string code = (await service.GetPrinterCode(Request())).TemporaryCode;
        await ClaimAsync(context);

        // Act
        string? token = await service.GetToken(code);

        // Assert
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
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();

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
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string code = (await service.GetPrinterCode(Request())).TemporaryCode;
        await ClaimAsync(context);

        PrusaConnectAuthenticationData stored = await context.PrusaConnectAuthentication.SingleAsync();
        stored.TemporaryCodeExpiry = DateTimeOffset.UtcNow.AddSeconds(-1);
        await context.SaveChangesAsync();

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
        await using PSDbContext context = await MigratedContextAsync();

        // Act
        Exception thrown = await Record.ExceptionAsync(() => NewService(context).GetToken("SECRET-CODE-VALUE"));

        // Assert
        thrown.Should().BeOfType<PrinterNotFoundException>();
        thrown.Message.Should().NotContain("SECRET-CODE-VALUE");
    }

    /// <summary>
    /// A code stops working the moment it has been redeemed for a token.
    /// </summary>
    /// <remarks>
    /// It used to stay live for the rest of its lifetime - up to <c>RegistrationCodeLifetimeMinutes</c>,
    /// an hour by default - so anyone else holding it could redeem it again.
    /// </remarks>
    [Fact]
    public async Task ARedeemedCodeCannotBeRedeemedAgain()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
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
    /// The half of this that bites hardest. Re-redeeming minted a fresh token and overwrote
    /// <c>HashedToken</c> with its hash, so the replay was not only a credential for the attacker but
    /// a denial of service against the real printer - whose token silently stopped authenticating.
    /// </remarks>
    [Fact]
    public async Task AReplayedCodeDoesNotInvalidateTheTokenAlreadyIssued()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string code = (await service.GetPrinterCode(Request())).TemporaryCode;
        await ClaimAsync(context);

        string token = (await service.GetToken(code))!;

        // Act
        await Record.ExceptionAsync(() => service.GetToken(code));

        // Assert
        PrusaConnectAuthenticationData stored = await context.PrusaConnectAuthentication.SingleAsync();

        new TokenService().VerifyToken(token, stored.HashedToken!).Should()
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
        await using PSDbContext context = await MigratedContextAsync();
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
    /// Issuing a token stamps when it was issued.
    /// </summary>
    /// <remarks>
    /// <c>TokenCreatedAt</c> was mapped and migrated but never written, so it read null for every row.
    /// Redemption is the only moment it can mean anything.
    /// </remarks>
    [Fact]
    public async Task IssuingATokenRecordsWhenItWasIssued()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        PrusaConnectService service = NewService(context);

        string code = (await service.GetPrinterCode(Request())).TemporaryCode;
        await ClaimAsync(context);

        // Act
        DateTimeOffset before = DateTimeOffset.UtcNow;
        await service.GetToken(code);

        // Assert
        PrusaConnectAuthenticationData stored = await context.PrusaConnectAuthentication.SingleAsync();

        stored.TokenCreatedAt.Should().NotBeNull();
        stored.TokenCreatedAt.Should().BeOnOrAfter(before.AddSeconds(-1)).And.BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(1));
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
        await using PSDbContext context = await MigratedContextAsync();
        using CapturingSink sink = new();

        // Act
        string code = (await NewService(context, logger: sink.AsLogger<PrusaConnectService>()).GetPrinterCode(Request())).TemporaryCode;

        // Assert
        sink.Entries.Should().NotBeEmpty("issuing a code is still worth an operational record");
        sink.Entries.Should().NotContainMatch($"*{code}*");
        sink.Entries.Should().NotContainMatch("*SUDBAJQ78CTJBNA8IHEMODUG43QD9H5GSBSFE0MMKBST8B9E0L*");
    }

    /// <summary>
    /// Renewing an expired code does not write the replacement to the log either.
    /// </summary>
    /// <remarks>
    /// The renewal branch is separate from the issue branch and leaked independently, so it needs its
    /// own guard.
    /// </remarks>
    [Fact]
    public async Task RenewingACodeDoesNotWriteTheReplacementToTheLog()
    {
        // Arrange
        await using PSDbContext context = await MigratedContextAsync();
        using CapturingSink sink = new();
        PrusaConnectService service = NewService(context, logger: sink.AsLogger<PrusaConnectService>());

        await service.GetPrinterCode(Request());

        PrusaConnectAuthenticationData stored = await context.PrusaConnectAuthentication.SingleAsync();
        stored.TemporaryCodeExpiry = DateTimeOffset.UtcNow.AddHours(-1);
        await context.SaveChangesAsync();

        // Act
        string renewed = (await service.GetPrinterCode(Request())).TemporaryCode;

        // Assert
        sink.Entries.Should().NotContainMatch($"*{renewed}*");
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
        await using PSDbContext context = await MigratedContextAsync();
        using CapturingSink sink = new();

        // Act
        await NewService(context, logger: sink.AsLogger<PrusaConnectService>()).GetPrinterCode(Request());

        // Assert
        PrusaConnectAuthenticationData stored = await context.PrusaConnectAuthentication.SingleAsync();

        stored.Id.Should().BeGreaterThan(0, "the key is assigned by the insert, so the log has to come after the save");
        sink.Entries.Should().ContainMatch($"RegistrationId={stored.Id}");
    }

    /// <summary>
    /// Captures what a sink would receive, through a real Serilog pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not a bare <see cref="ILogger{T}"/> stub. The first version of this was, and it
    /// made the fingerprint half of these assertions vacuous: <c>Microsoft.Extensions.Logging</c>'s
    /// default formatter calls <see cref="object.ToString"/> on the argument, so <c>{@Printer}</c>
    /// captured the literal string <c>PrinterService.Host.PrusaConnect.DTO.RegisterPrinterRequestDTO</c>
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

        public void Emit(LogEvent logEvent) => _events.Add(logEvent);

        public ILogger<T> AsLogger<T>()
        {
            // Owned rather than left to the finalizer so the pipeline is torn down with the test.
            _factory ??= new SerilogLoggerFactory(
                new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(this).CreateLogger());

            return _factory.CreateLogger<T>();
        }

        public void Dispose() => _factory?.Dispose();

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

    private static async Task ClaimAsync(PSDbContext context)
    {
        // A printer belongs to a team, and foreign keys are enforced, so the owning team has to
        // exist before the printer can reference it.
        Team team = new()
        {
            CreatedBy = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        context.Teams.Add(team);
        await context.SaveChangesAsync();

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
        await context.SaveChangesAsync();

        PrusaConnectAuthenticationData auth = await context.PrusaConnectAuthentication.SingleAsync();
        auth.PrinterId = printer.Id;
        await context.SaveChangesAsync();
    }
}
