using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.PrintFiles;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrintFileSender"/> chooses the download by the printer's connection, and cleans up
/// every store it touched on every path the printer will never come for the bytes by.
/// </summary>
/// <remarks>
/// The choice is the load-bearing part: <c>START_CONNECT_DOWNLOAD</c> sent to a printer that cannot
/// stream chunks starts a transfer whose chunk request has no address, and firmware asserts on it -
/// so a wrong answer here is a wedged printer, not a failed test. The cleanup is the rule the class
/// exists to keep in one place, now with two stores instead of one.
/// </remarks>
public sealed class PrintFileSenderTests : IDisposable
{
    private const int PrinterId = 1;
    private const long Owner = 10;
    private const ushort TransferPort = 15080;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-sender-{Guid.NewGuid():N}.db");
    private readonly string _fileDirectory = Path.Combine(Path.GetTempPath(), $"hs-sender-files-{Guid.NewGuid():N}");
    private readonly PrinterConnectionRegistry _registry = new(NullLogger<PrinterConnectionRegistry>.Instance);
    private readonly TransferOfferStore _offers = new(TimeProvider.System, NullLogger<TransferOfferStore>.Instance);
    private readonly EncryptedTransferOffers _encrypted = new();

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        if (Directory.Exists(_fileDirectory))
        {
            Directory.Delete(_fileDirectory, recursive: true);
        }
    }

    /// <summary>
    /// A printer whose connection streams chunks - a socket - is sent the inline command, offered
    /// under a random token. Unchanged behaviour, pinned so the new branch cannot have stolen it.
    /// </summary>
    [Fact]
    public async Task AChunkStreamingPrinterIsSentTheInlineDownload()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        StoredFile file = WriteFile("model.gcode", 4096);
        IPrinterConnectionActor actor = Connect(canStreamChunks: true, PrinterEventType.Finished);

        // Act
        FileSendResult result = await NewSender(context).SendAsync(
            await context.Printers.SingleAsync(TestContext.Current.CancellationToken),
            file, Caller.Unscoped(Owner), TestContext.Current.CancellationToken);

        // Assert
        ISendableCommand sent = SentCommand(actor);

        StartConnectDownload inline = sent.Should().BeOfType<StartConnectDownload>().Which;
        result.WireName.Should().Be(sent.WireName, "a refusal names the command the caller reports");
        _offers.TryOpen(inline.Hash, out ITransferContent? content).Should().BeTrue("the bytes are offered under the token the command carries");
        content!.Dispose();
    }

    /// <summary>
    /// A printer whose connection cannot stream chunks - the pre-websocket transport - is sent the
    /// encrypted download: fresh key and IV, the transfer port named, the bytes offered under the
    /// IV's hex, and the key registered beside them so <c>/f/&lt;iv&gt;/raw</c> can serve it.
    /// </summary>
    [Fact]
    public async Task ANonStreamingPrinterIsSentTheEncryptedDownloadWithTheTransferPort()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        StoredFile file = WriteFile("model.gcode", 4096);
        IPrinterConnectionActor actor = Connect(canStreamChunks: false, PrinterEventType.Finished);

        // Act
        FileSendResult result = await NewSender(context).SendAsync(
            await context.Printers.SingleAsync(TestContext.Current.CancellationToken),
            file, Caller.Unscoped(Owner), TestContext.Current.CancellationToken);

        // Assert
        StartEncryptedDownload encrypted = SentCommand(actor).Should().BeOfType<StartEncryptedDownload>().Which;

        // The bug this closes: the caller used to name START_CONNECT_DOWNLOAD here, which is the
        // other branch - so every refusal on the pre-websocket transport pointed at the wrong code.
        result.WireName.Should().Be(StartEncryptedDownload.Wire);
        result.WireName.Should().NotBe(StartConnectDownload.Wire);

        encrypted.Port.Should().Be(TransferPort, "firmware would otherwise fetch from its enrolled port, rewriting 443 to 80");
        encrypted.Key.Should().HaveCount(TransferCipher.KeyLength);
        encrypted.Iv.Should().HaveCount(TransferCipher.IvLength);
        encrypted.OriginalSize.Should().Be(4096);

        string ivHex = Convert.ToHexStringLower(encrypted.Iv);

        _offers.TryOpen(ivHex, out ITransferContent? content).Should().BeTrue("the bytes are offered under the IV the printer will ask for");
        content!.Dispose();

        EncryptedTransfer? registered = _encrypted.Find(ivHex);
        registered.Should().NotBeNull("the key must be findable by the endpoint");
        registered!.Key.Should().Equal(encrypted.Key, "and be the very key the printer was told");
        registered.OfferToken.Should().Be(ivHex);
    }

    /// <summary>
    /// Two sends mint two IVs. CTR under a repeated (key, IV) reuses a keystream, which leaks the XOR
    /// of the two files; this is the property <see cref="TransferCipher"/>'s remarks call the one
    /// fatal shortcut, so it is pinned rather than assumed.
    /// </summary>
    [Fact]
    public async Task EverySendMintsAFreshKeyAndIv()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        StoredFile file = WriteFile("model.gcode", 4096);
        IPrinterConnectionActor actor = Connect(canStreamChunks: false, PrinterEventType.Finished);
        Printer printer = await context.Printers.SingleAsync(TestContext.Current.CancellationToken);
        PrintFileSender sender = NewSender(context);

        // Act
        await sender.SendAsync(printer, file, Caller.Unscoped(Owner), TestContext.Current.CancellationToken);
        await sender.SendAsync(printer, file, Caller.Unscoped(Owner), TestContext.Current.CancellationToken);

        // Assert
        StartEncryptedDownload[] sent = SentCommands(actor).Cast<StartEncryptedDownload>().ToArray();

        sent.Should().HaveCount(2);
        sent[0].Iv.Should().NotEqual(sent[1].Iv);
        sent[0].Key.Should().NotEqual(sent[1].Key);
    }

    /// <summary>
    /// A printer that rejects the encrypted download will never fetch, so both stores are cleaned:
    /// the offer holding the descriptor, and the key beside it. Half a cleanup would be a pinned
    /// file with no key to serve it, or a key with no file - each a leak of a different kind.
    /// </summary>
    [Fact]
    public async Task ARejectedEncryptedDownloadRevokesBothTheOfferAndTheKey()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        StoredFile file = WriteFile("model.gcode", 4096);
        IPrinterConnectionActor actor = Connect(canStreamChunks: false, PrinterEventType.Rejected, "Not allowed outside /usb");

        // Act
        await NewSender(context).SendAsync(await context.Printers.SingleAsync(TestContext.Current.CancellationToken),
                                           file, Caller.Unscoped(Owner), TestContext.Current.CancellationToken);

        // Assert
        StartEncryptedDownload encrypted = SentCommand(actor).Should().BeOfType<StartEncryptedDownload>().Which;
        string ivHex = Convert.ToHexStringLower(encrypted.Iv);

        _offers.TryOpen(ivHex, out _).Should().BeFalse("the offer was revoked");
        _encrypted.Find(ivHex).Should().BeNull("and so was the key");
    }

    /// <summary>
    /// A send that throws - the printer vanishing mid-send, say - cleans up the same two stores.
    /// The throw propagates; the cleanup happens first.
    /// </summary>
    [Fact]
    public async Task AThrowingEncryptedSendRevokesBothStoresBeforePropagating()
    {
        // Arrange
        await using HomespoolDbContext context = await SeedAsync();
        StoredFile file = WriteFile("model.gcode", 4096);

        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        actor.CanStreamChunks.Returns(false);
        actor.SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>())
             .Returns<Task<CommandSendResult>>(_ => throw new InvalidOperationException("gone"));
        _registry.Register(PrinterId, actor);

        // Act
        Func<Task> act = async () => await NewSender(context).SendAsync(
            await context.Printers.SingleAsync(TestContext.Current.CancellationToken),
            file, Caller.Unscoped(Owner), TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();

        StartEncryptedDownload encrypted = SentCommand(actor).Should().BeOfType<StartEncryptedDownload>().Which;
        string ivHex = Convert.ToHexStringLower(encrypted.Iv);

        _offers.TryOpen(ivHex, out _).Should().BeFalse();
        _encrypted.Find(ivHex).Should().BeNull();
    }

    private static ISendableCommand SentCommand(IPrinterConnectionActor actor)
    {
        return SentCommands(actor).FirstOrDefault() ?? throw new InvalidOperationException("No command was sent.");
    }

    private static IEnumerable<ISendableCommand> SentCommands(IPrinterConnectionActor actor)
    {
        return actor.ReceivedCalls()
                    .Where(call => call.GetMethodInfo().Name == nameof(IPrinterConnectionActor.SendCommandAsync))
                    .Select(call => (ISendableCommand)call.GetArguments()[0]!);
    }

    private IPrinterConnectionActor Connect(bool canStreamChunks, PrinterEventType reply, string? reason = null)
    {
        IPrinterConnectionActor actor = Substitute.For<IPrinterConnectionActor>();
        actor.IsOpen.Returns(true);
        actor.CanStreamChunks.Returns(canStreamChunks);
        actor.SendCommandAsync(Arg.Any<ISendableCommand>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new CommandSendResult(CommandSendOutcome.Completed,
                                                            new CommandOutcome(reply, reason))));

        _registry.Register(PrinterId, actor);

        return actor;
    }

    private PrintFileSender NewSender(HomespoolDbContext context)
    {
        PrinterCommandService commands = new(
            new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance),
            _registry);

        return new PrintFileSender(_offers,
                                   _encrypted,
                                   commands,
                                   Options.Create(new PrusaConnectOptions { TransferPort = TransferPort }));
    }

    private StoredFile WriteFile(string name, int length)
    {
        Directory.CreateDirectory(_fileDirectory);

        string path = Path.Combine(_fileDirectory, name);
        File.WriteAllBytes(path, new byte[length]);

        return new StoredFile(name, path, length, DateTimeOffset.UtcNow);
    }

    private HomespoolDbContext NewContext()
    {
        return new HomespoolDbContext(new DbContextOptionsBuilder<HomespoolDbContext>()
                                      .UseSqlite($"Data Source={_databasePath}")
                                      .Options);
    }

    private async Task<HomespoolDbContext> SeedAsync()
    {
        HomespoolDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        const string email = "owner@example.com";

        context.Users.Add(new HSUser(email)
        {
            Id = Owner,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
        });

        Team team = new() { Name = "team" };
        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = Owner, Capabilities = TestMemberships.Graded(true, true, false) });
        context.Printers.Add(new Printer { Id = PrinterId, Uuid = Guid.NewGuid(), TeamId = team.Id });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return context;
    }
}
