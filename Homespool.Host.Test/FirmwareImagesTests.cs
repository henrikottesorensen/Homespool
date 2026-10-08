using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.Firmware;
using Homespool.Host.PrintFiles;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="FirmwareImages"/> - only an intact, signed image that fits the printer is stored, by
/// somebody who manages that printer, and only such an image is offered.
/// </summary>
/// <remarks>
/// A real SQLite file and a real directory: the refusals are about what is left behind on both, and a
/// fake of either would let a leftover go unseen.
/// </remarks>
public sealed class FirmwareImagesTests : IDisposable
{
    private const long Manager = 1;
    private const long Operator = 2;
    private const long OtherManager = 3;
    private const string ImageName = "COREONE_firmware_7.0.0.bbf";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "homespool-firmware-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-firmware-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task AVerifiedImageThatFitsIsStoredAndOffered()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, "7.1.0");

        // Act
        FirmwareImage stored = await StoreAsync(context, printer, TestFirmwareImages.Build());
        IReadOnlyList<FirmwareImage> offered = await NewImages(context).ListForAsync(Caller.Unscoped(Manager), printer.Id,
                                                                                  TestContext.Current.CancellationToken);

        // Assert
        stored.Name.Should().Be(ImageName);
        stored.Header.Version.Should().Be("7.0.0+16903");
        offered.Should().ContainSingle().Which.Digest.Should().Be(stored.Digest);

        HSFile row = await context.Files.SingleAsync(TestContext.Current.CancellationToken);
        row.Type.Should().Be(FileType.PrusaFirmware);
        row.UserId.Should().Be(Manager);
        row.MetadataState.Should().Be(PrintFileMetadataState.Silent);

        Directory.EnumerateFiles(_root).Select(Path.GetFileName).Should().Equal(stored.Digest + ".bbf");
        Directory.EnumerateFileSystemEntries(Path.Combine(_root, ".incoming")).Should().BeEmpty();
    }

    /// <summary>A refusal stores nothing: no row, and no bytes left anywhere under the store.</summary>
    [Fact]
    public async Task AnImageThatIsNotVerifiedIsRefusedAndLeavesNothing()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, "7.1.0");

        // Act
        Func<Task> store = () => StoreAsync(context, printer, TestFirmwareImages.Build(signed: false));

        // Assert
        FirmwareImageRefusedException refused = (await store.Should().ThrowAsync<FirmwareImageRefusedException>()).Which;
        refused.Refusal.Should().Be(FirmwareImageRefusal.NotVerified);
        refused.Check!.Verdict.Should().Be(PrusaFirmwareVerdict.NoSignature);
        refused.ResourceKey.Should().Be("Error_FirmwareNoSignature");

        (await context.Files.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task AnImageForAnotherKindOfPrinterIsRefused()
    {
        // Arrange - an MK4 build, offered to a Core One
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, "7.1.0");

        // Act
        Func<Task> store = () => StoreAsync(context, printer, TestFirmwareImages.Build(printerType: 1, printerVersion: 4));

        // Assert
        (await store.Should().ThrowAsync<FirmwareImageRefusedException>())
            .Which.Refusal.Should().Be(FirmwareImageRefusal.WrongPrinter);
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    /// <summary>
    /// A printer that has not sent its <c>INFO</c> has no model to match against, so nothing can be
    /// said to fit it - refused, rather than stored on the assumption that it will.
    /// </summary>
    [Fact]
    public async Task AnImageForAPrinterThatHasNotSaidWhatItIsIsRefused()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, model: null);

        // Act
        Func<Task> store = () => StoreAsync(context, printer, TestFirmwareImages.Build());

        // Assert
        (await store.Should().ThrowAsync<FirmwareImageRefusedException>())
            .Which.Refusal.Should().Be(FirmwareImageRefusal.PrinterModelUnknown);
    }

    [Fact]
    public async Task ANameThatIsNotAnImageNameIsRefused()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, "7.1.0");

        // Act
        Func<Task> store = () => StoreAsync(context, printer, TestFirmwareImages.Build(), "firmware.gcode");

        // Assert
        (await store.Should().ThrowAsync<FirmwareImageRefusedException>())
            .Which.Refusal.Should().Be(FirmwareImageRefusal.NotAnImageName);
    }

    /// <summary>
    /// Storing happens on the printer's behalf, so it needs the printer's management - being on its
    /// team is not enough.
    /// </summary>
    [Fact]
    public async Task SomebodyWhoCannotManageThePrinterIsRefused()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, "7.1.0");

        // Act
        Func<Task> store = () => StoreAsync(context, printer, TestFirmwareImages.Build(), caller: Operator);

        // Assert
        await store.Should().ThrowAsync<TeamAccessDeniedException>();
        (await context.Files.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>The same bytes uploaded again, by anybody under any name, are the one image.</summary>
    [Fact]
    public async Task TheSameImageUploadedTwiceIsOneImage()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, "7.1.0");
        byte[] image = TestFirmwareImages.Build();

        // Act
        FirmwareImage first = await StoreAsync(context, printer, image);
        FirmwareImage second = await StoreAsync(context, printer, image, "renamed.bbf");

        // Assert
        second.Digest.Should().Be(first.Digest);
        second.Name.Should().Be(ImageName);
        (await context.Files.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    /// <summary>
    /// The same firmware in other bytes - its signature in the other valid form, the tarballs in each
    /// other's places - is the one image too, kept as first uploaded, so nobody is offered a look-alike
    /// beside it.
    /// </summary>
    [Fact]
    public async Task TheSameFirmwareInOtherBytesIsOneImage()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, "7.1.0");
        byte[] image = TestFirmwareImages.Build();
        byte[] otherSignature = TestFirmwareImages.WithOtherSignature(image);
        byte[] swapped = TestFirmwareImages.Build(entries: TestFirmwareImages.Entries(TestFirmwareImages.BootloaderTarball,
                                                                                      TestFirmwareImages.ResourcesTarball));

        // Act
        FirmwareImage first = await StoreAsync(context, printer, image);
        FirmwareImage second = await StoreAsync(context, printer, otherSignature, "twin.bbf");
        FirmwareImage third = await StoreAsync(context, printer, swapped, "swapped.bbf");

        // Assert
        otherSignature.Should().NotEqual(image);
        second.Digest.Should().Be(first.Digest);
        third.Digest.Should().Be(first.Digest);
        (await context.Files.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await File.ReadAllBytesAsync(Path.Combine(_root, first.Digest + ".bbf"), TestContext.Current.CancellationToken))
            .Should().Equal(image);
    }

    /// <summary>The reviewer's case: Prusa's firmware with a byte of its resources changed.</summary>
    [Fact]
    public async Task AnImageWithChangedResourcesIsRefusedAndLeavesNothing()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, "7.1.0");
        byte[] image = TestFirmwareImages.Build();
        image[^100] ^= 1;

        // Act
        Func<Task> store = () => StoreAsync(context, printer, image);

        // Assert
        FirmwareImageRefusedException refused = (await store.Should().ThrowAsync<FirmwareImageRefusedException>()).Which;
        refused.ResourceKey.Should().Be("Error_FirmwareResourcesChanged");
        (await context.Files.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task AnImageWhoseResourcesCannotBeReadIsRefused()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, "7.1.0");

        // Act
        Func<Task> store = () => StoreAsync(context, printer, [.. TestFirmwareImages.Build(), 0]);

        // Assert
        (await store.Should().ThrowAsync<FirmwareImageRefusedException>())
            .Which.ResourceKey.Should().Be("Error_FirmwareResourcesUnreadable");
    }

    [Fact]
    public async Task ADifferentImageUnderANameAlreadyUsedIsRefused()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, "7.1.0");
        await StoreAsync(context, printer, TestFirmwareImages.Build());

        // Act
        Func<Task> store = () => StoreAsync(context, printer, TestFirmwareImages.Build(seed: 1));

        // Assert
        (await store.Should().ThrowAsync<FirmwareImageRefusedException>())
            .Which.Refusal.Should().Be(FirmwareImageRefusal.NameTaken);
    }

    /// <summary>
    /// Images are shared, but each printer is offered only the ones built for it: a Core One image is
    /// stored once and listed for the Core One, not for an MK4 on the same team.
    /// </summary>
    [Fact]
    public async Task APrinterIsOfferedOnlyTheImagesThatFitIt()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer coreOne = await AddPrinterAsync(context, "7.1.0");
        Printer mk4 = await AddPrinterAsync(context, "1.4.0", teamId: coreOne.TeamId);
        await StoreAsync(context, coreOne, TestFirmwareImages.Build());

        // Act
        IReadOnlyList<FirmwareImage> forCoreOne = await NewImages(context).ListForAsync(Caller.Unscoped(Manager), coreOne.Id,
                                                                                     TestContext.Current.CancellationToken);
        IReadOnlyList<FirmwareImage> forMk4 = await NewImages(context).ListForAsync(Caller.Unscoped(Manager), mk4.Id,
                                                                                 TestContext.Current.CancellationToken);

        // Assert
        forCoreOne.Should().ContainSingle();
        forMk4.Should().BeEmpty();
    }

    /// <summary>
    /// The disk is checked again on every listing, so an image whose bytes changed underneath the row
    /// stops being offered rather than being believed because it was once good.
    /// </summary>
    [Fact]
    public async Task AnImageChangedOnDiskIsNoLongerOffered()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, "7.1.0");
        FirmwareImage stored = await StoreAsync(context, printer, TestFirmwareImages.Build());

        string path = Path.Combine(_root, stored.Digest + ".bbf");
        byte[] bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        bytes[PrusaFirmwareVerifier.FirmwareOffset + 1] ^= 1;
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        // Act
        IReadOnlyList<FirmwareImage> offered = await NewImages(context).ListForAsync(Caller.Unscoped(Manager), printer.Id,
                                                                                  TestContext.Current.CancellationToken);

        // Assert
        offered.Should().BeEmpty();
    }

    /// <summary>Uploading an image again is the remedy for its file having gone missing.</summary>
    [Fact]
    public async Task UploadingAnImageAgainRestoresAFileThatWentMissing()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, "7.1.0");
        byte[] image = TestFirmwareImages.Build();
        FirmwareImage stored = await StoreAsync(context, printer, image);
        File.Delete(Path.Combine(_root, stored.Digest + ".bbf"));

        // Act
        await StoreAsync(context, printer, image);
        IReadOnlyList<FirmwareImage> offered = await NewImages(context).ListForAsync(Caller.Unscoped(Manager), printer.Id,
                                                                                  TestContext.Current.CancellationToken);

        // Assert
        offered.Should().ContainSingle();
    }

    [Fact]
    public async Task AnUploadLargerThanAnyImageIsCutOff()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, "7.1.0");
        await using Stream endless = new EndlessStream();

        // Act
        Func<Task> store = () => NewImages(context).StoreAsync(Caller.Unscoped(Manager), printer.Id, ImageName, endless,
                                                               TestContext.Current.CancellationToken);

        // Assert
        await store.Should().ThrowAsync<UploadTooLargeException>();
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    /// <summary>
    /// An image Homespool has recorded on a printer's drive - an install whose clean-up did not go
    /// through - is kept: the record is what that printer's next install clears the drive by, and it
    /// would go with the image.
    /// </summary>
    [Fact]
    public async Task AnImageRecordedOnAPrintersDriveIsNotDeleted()
    {
        // Arrange - recorded on another team's printer, deleted by this one's manager
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, "7.1.0");
        Printer othersPrinter = await AddPrinterOnAnotherTeamAsync(context, "7.1.0");
        FirmwareImage stored = await StoreAsync(context, printer, TestFirmwareImages.Build());
        long fileId = await context.Files.Select(row => row.Id).SingleAsync(TestContext.Current.CancellationToken);

        context.FilesOnPrinters.Add(new FileOnPrinter { PrinterId = othersPrinter.Id, FileId = fileId, PrinterPath = "/usb/FIRMWARE.BBF" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        Func<Task> delete = () => NewImages(context).DeleteAsync(Caller.Unscoped(Manager), printer.Id, stored.Digest,
                                                                 TestContext.Current.CancellationToken);

        // Assert
        (await delete.Should().ThrowAsync<FirmwareImageRefusedException>()).Which.ResourceKey.Should().Be("Error_FirmwareOnAPrinter");
        (await context.Files.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await context.FilesOnPrinters.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        File.Exists(Path.Combine(_root, stored.Digest + ".bbf")).Should().BeTrue();
    }

    /// <summary>
    /// Nor while it is being installed, before the send has recorded anything: the install is reading
    /// it from the store.
    /// </summary>
    [Fact]
    public async Task AnImageBeingInstalledIsNotDeleted()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, "7.1.0");
        FirmwareImage stored = await StoreAsync(context, printer, TestFirmwareImages.Build());
        long fileId = await context.Files.Select(row => row.Id).SingleAsync(TestContext.Current.CancellationToken);

        IFirmwareInstallations installations = Substitute.For<IFirmwareInstallations>();
        installations.IsInstallingImage(fileId).Returns(true);

        // Act
        Func<Task> delete = () => NewImages(context, installations).DeleteAsync(Caller.Unscoped(Manager), printer.Id, stored.Digest,
                                                                                TestContext.Current.CancellationToken);

        // Assert
        (await delete.Should().ThrowAsync<FirmwareImageRefusedException>()).Which.Refusal.Should().Be(FirmwareImageRefusal.OnAPrinter);
        File.Exists(Path.Combine(_root, stored.Digest + ".bbf")).Should().BeTrue();
    }

    /// <summary>
    /// Images are shared, so deleting one is for whoever manages a printer it fits - here somebody on
    /// another team entirely, who did not upload it.
    /// </summary>
    [Fact]
    public async Task AManagerOfAnyPrinterAnImageFitsMayDeleteIt()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer uploadersPrinter = await AddPrinterAsync(context, "7.1.0");
        Printer othersPrinter = await AddPrinterOnAnotherTeamAsync(context, "7.2.0");
        FirmwareImage stored = await StoreAsync(context, uploadersPrinter, TestFirmwareImages.Build());

        // Act
        string? deleted = await NewImages(context).DeleteAsync(Caller.Unscoped(OtherManager), othersPrinter.Id, stored.Digest,
                                                               TestContext.Current.CancellationToken);

        // Assert
        deleted.Should().Be(ImageName);
        (await context.Files.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        File.Exists(Path.Combine(_root, stored.Digest + ".bbf")).Should().BeFalse();
    }

    /// <summary>
    /// Through a printer the image does not fit, it is not there to delete - as the page for that
    /// printer never lists it.
    /// </summary>
    [Fact]
    public async Task AnImageIsNotDeletedThroughAPrinterItDoesNotFit()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer coreOne = await AddPrinterAsync(context, "7.1.0");
        Printer mk4 = await AddPrinterAsync(context, "1.4.0", teamId: coreOne.TeamId);
        FirmwareImage stored = await StoreAsync(context, coreOne, TestFirmwareImages.Build());

        // Act
        string? deleted = await NewImages(context).DeleteAsync(Caller.Unscoped(Manager), mk4.Id, stored.Digest,
                                                               TestContext.Current.CancellationToken);

        // Assert
        deleted.Should().BeNull();
        (await context.Files.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        File.Exists(Path.Combine(_root, stored.Digest + ".bbf")).Should().BeTrue();
    }

    [Fact]
    public async Task SomebodyWhoCannotManageThePrinterCannotDeleteFromIt()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, "7.1.0");
        FirmwareImage stored = await StoreAsync(context, printer, TestFirmwareImages.Build());

        // Act
        Func<Task> delete = () => NewImages(context).DeleteAsync(Caller.Unscoped(Operator), printer.Id, stored.Digest,
                                                                 TestContext.Current.CancellationToken);

        // Assert
        await delete.Should().ThrowAsync<TeamAccessDeniedException>();
        (await context.Files.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task AnUnknownImageDeletesNothing()
    {
        // Arrange
        await using HomespoolDbContext context = await MigratedContextAsync();
        Printer printer = await AddPrinterAsync(context, "7.1.0");
        await StoreAsync(context, printer, TestFirmwareImages.Build());

        // Act
        string? deleted = await NewImages(context).DeleteAsync(Caller.Unscoped(Manager), printer.Id, "not-a-digest",
                                                               TestContext.Current.CancellationToken);

        // Assert
        deleted.Should().BeNull();
        (await context.Files.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    /// <summary>A printer reporting <paramref name="model"/> on a team that <see cref="OtherManager"/> alone manages.</summary>
    private static async Task<Printer> AddPrinterOnAnotherTeamAsync(HomespoolDbContext context, string model)
    {
        TestAccounts.Add(context, OtherManager);

        Team team = new()
        {
            CreatedBy = OtherManager,
            CreatedAt = DateTimeOffset.UtcNow,
            Members =
            {
                new TeamMember { UserId = OtherManager, Capabilities = TestMemberships.Literal(CapabilityPresets.Manager), IsDefault = true },
            },
        };

        context.Teams.Add(team);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return await AddPrinterAsync(context, model, team.Id);
    }

    private async Task<FirmwareImage> StoreAsync(HomespoolDbContext context,
                                                 Printer printer,
                                                 byte[] image,
                                                 string name = ImageName,
                                                 long caller = Manager)
    {
        await using MemoryStream content = new(image, writable: false);

        return await NewImages(context).StoreAsync(Caller.Unscoped(caller), printer.Id, name, content,
                                                   TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A printer reporting <paramref name="model"/>, on a team where <see cref="Manager"/> manages and
    /// <see cref="Operator"/> only operates - a new team unless one is given.
    /// </summary>
    private static async Task<Printer> AddPrinterAsync(HomespoolDbContext context, string? model, int? teamId = null)
    {
        if (teamId is null)
        {
            TestAccounts.Add(context, Manager, Operator);

            Team team = new()
            {
                CreatedBy = Manager,
                CreatedAt = DateTimeOffset.UtcNow,
                Members =
                {
                    new TeamMember { UserId = Manager, Capabilities = TestMemberships.Literal(CapabilityPresets.Manager), IsDefault = true },
                    new TeamMember { UserId = Operator, Capabilities = TestMemberships.Literal(CapabilityPresets.Operator), IsDefault = true },
                },
            };

            context.Teams.Add(team);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            teamId = team.Id;
        }

        Printer printer = new()
        {
            Uuid = Guid.NewGuid(),
            Type = PrinterType.PrusaConnect,
            TeamId = teamId.Value,
            Model = model,
            Status = PrinterStatus.Idle,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return printer;
    }

    private FirmwareImages NewImages(HomespoolDbContext context, IFirmwareInstallations? installations = null)
    {
        return new FirmwareImages(new PrinterAccessService(context, NullLogger<PrinterAccessService>.Instance),
                                  context,
                                  TestFirmwareImages.Verifier,
                                  installations ?? Substitute.For<IFirmwareInstallations>(),
                                  TestOptions.Monitor(new FirmwareStorageOptions { Directory = _root }),
                                  new HostEnvironmentAccessor(_root),
                                  TimeProvider.System,
                                  NullLogger<FirmwareImages>.Instance);
    }

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        HomespoolDbContext context = new(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }

    /// <summary>A body that never ends, so only a limit can stop reading it.</summary>
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);

            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Clear();

            return ValueTask.FromResult(buffer.Length);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
