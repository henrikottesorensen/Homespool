using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="UserFileStore"/> - each user's folder of print files, and what it refuses.
/// </summary>
/// <remarks>
/// Two properties carry most of the weight here, and neither is about happy-path storage. <b>A
/// caller-supplied name reaches the filesystem</b>, so the tests are largely about what cannot
/// happen - the analyzer suppression on that class asserts these constraints hold, and this is where
/// the claim is checked. And <b>ownership is the directory</b>, so one user reaching another's file
/// has to be impossible rather than merely unimplemented.
/// </remarks>
public sealed class UserFileStoreTests : IDisposable
{
    private const long Alice = 1;
    private const long Bob = 2;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "homespool-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ASavedFileIsFoundAgainWithItsNameLengthAndContent()
    {
        // Arrange
        UserFileStore store = NewStore();
        byte[] content = Encoding.UTF8.GetBytes("G28 ; home\nG1 X10\n");

        // Act
        StoredFile saved = await SaveAsync(store, Alice, "model.bgcode", content);
        StoredFile? found = store.Find(Alice, "model.bgcode");

        // Assert
        found.Should().NotBeNull();
        found!.FileName.Should().Be("model.bgcode", "the name is the identity, and the name it takes on the printer");
        found.Length.Should().Be(content.Length);
        found.PrinterPath.Should().Be("/usb/model.bgcode");
        (await File.ReadAllBytesAsync(found.Path)).Should().Equal(content);
        saved.Path.Should().Be(found.Path);
    }

    /// <summary>
    /// The property the whole layout exists for: a file is reachable only through the id of the user
    /// who owns it, so there is no way to ask this class for someone else's file.
    /// </summary>
    [Fact]
    public async Task OneUsersFileIsInvisibleToAnother()
    {
        // Arrange
        UserFileStore store = NewStore();
        await SaveAsync(store, Alice, "secret.gcode", [1, 2, 3]);

        // Act
        StoredFile? asBob = store.Find(Bob, "secret.gcode");
        IReadOnlyList<StoredFile> bobsFiles = store.List(Bob);

        // Assert
        asBob.Should().BeNull("ownership is which directory a file is in, not a flag on it");
        bobsFiles.Should().BeEmpty();
        store.Delete(Bob, "secret.gcode").Should().BeFalse("nor can one user delete another's file");
        store.Find(Alice, "secret.gcode").Should().NotBeNull("and the attempt leaves it untouched");
    }

    [Fact]
    public async Task TwoUsersMayHoldTheSameNameIndependently()
    {
        // Arrange
        UserFileStore store = NewStore();

        // Act
        await SaveAsync(store, Alice, "benchy.gcode", Encoding.UTF8.GetBytes("alice"));
        await SaveAsync(store, Bob, "benchy.gcode", Encoding.UTF8.GetBytes("bob"));

        // Assert
        (await File.ReadAllTextAsync(store.Find(Alice, "benchy.gcode")!.Path)).Should().Be("alice");
        (await File.ReadAllTextAsync(store.Find(Bob, "benchy.gcode")!.Path)).Should().Be("bob");
    }

    /// <summary>
    /// Overwriting is opt-in. A re-slice produces the same name with new content and so does an
    /// accident; only the first should be able to happen without saying so.
    /// </summary>
    [Fact]
    public async Task AnExistingNameIsRefusedUnlessOverwriteIsAskedFor()
    {
        // Arrange
        UserFileStore store = NewStore();
        await SaveAsync(store, Alice, "benchy.gcode", Encoding.UTF8.GetBytes("first"));

        // Act
        Func<Task> act = () => SaveAsync(store, Alice, "benchy.gcode", Encoding.UTF8.GetBytes("second"));

        // Assert
        await act.Should().ThrowAsync<PrintFileNameConflictException>();
        (await File.ReadAllTextAsync(store.Find(Alice, "benchy.gcode")!.Path)).Should().Be("first",
            "a refused upload must not have replaced anything");
        store.List(Alice).Should().ContainSingle("nor left a second copy behind");
    }

    [Fact]
    public async Task OverwriteReplacesTheContentAndKeepsOneFile()
    {
        // Arrange
        UserFileStore store = NewStore();
        await SaveAsync(store, Alice, "benchy.gcode", Encoding.UTF8.GetBytes("first"));

        // Act
        StoredFile replaced = await SaveAsync(store, Alice, "benchy.gcode", Encoding.UTF8.GetBytes("second!"), overwrite: true);

        // Assert
        (await File.ReadAllTextAsync(replaced.Path)).Should().Be("second!");
        replaced.Length.Should().Be(7);
        store.List(Alice).Should().ContainSingle();
    }

    /// <summary>
    /// The filesystem underneath may fold case or may not; the store's answer must not depend on
    /// that, because the printer's FAT32 would collide the two names at <c>/usb/</c> regardless.
    /// </summary>
    [Fact]
    public async Task NamesCollideRegardlessOfCase()
    {
        // Arrange
        UserFileStore store = NewStore();
        await SaveAsync(store, Alice, "Benchy.gcode", Encoding.UTF8.GetBytes("first"));

        // Act
        Func<Task> act = () => SaveAsync(store, Alice, "benchy.GCODE", Encoding.UTF8.GetBytes("second"));

        // Assert
        await act.Should().ThrowAsync<PrintFileNameConflictException>();
        store.Find(Alice, "BENCHY.gcode").Should().NotBeNull("and a lookup folds case the same way");
        store.Find(Alice, "BENCHY.gcode")!.FileName.Should().Be("Benchy.gcode",
            "the name reported is the one on disk, not the spelling that was asked for");
    }

    [Fact]
    public async Task OverwritingADifferentlyCasedNameLeavesOneFile()
    {
        // Arrange
        UserFileStore store = NewStore();
        await SaveAsync(store, Alice, "Benchy.gcode", Encoding.UTF8.GetBytes("first"));

        // Act
        StoredFile replaced = await SaveAsync(store, Alice, "benchy.gcode", Encoding.UTF8.GetBytes("second"), overwrite: true);

        // Assert
        store.List(Alice).Should().ContainSingle("the old spelling must not survive beside the new one");
        replaced.FileName.Should().Be("benchy.gcode");
        (await File.ReadAllTextAsync(replaced.Path)).Should().Be("second");
    }

    /// <summary>
    /// A name is a name, not a path. Anything that looks like a directory is discarded rather than
    /// escaped or rejected - <c>Path.GetFileName</c> leaves nothing that can traverse.
    /// </summary>
    [Theory]
    [InlineData("../../../etc/passwd.gcode", "passwd.gcode")]
    [InlineData("/absolute/path/model.gcode", "model.gcode")]
    [InlineData("subdir/model.gcode", "model.gcode")]
    public async Task ADirectoryComponentInTheNameIsDiscarded(string given, string expected)
    {
        // Arrange
        UserFileStore store = NewStore();

        // Act
        StoredFile saved = await SaveAsync(store, Alice, given, [1, 2, 3]);

        // Assert
        saved.FileName.Should().Be(expected);
        Path.GetFullPath(saved.Path).Should().StartWith(Path.GetFullPath(_root),
            "nothing a caller supplies may place a file outside the store");
    }

    /// <summary>
    /// <c>Path.GetFileName</c> passes <c>..</c> through unchanged, so it is refused explicitly. This
    /// is the one traversal the reduction does not handle by itself.
    /// </summary>
    [Theory]
    [InlineData("some/directory/")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ANameThatIsNotAFileNameIsRejected(string given)
    {
        // Arrange
        UserFileStore store = NewStore();

        // Act
        Func<Task> act = () => SaveAsync(store, Alice, given, [1]);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AnExtensionNoPrinterAcceptsIsRefusedByTheStoreItself()
    {
        // Arrange
        UserFileStore store = NewStore();

        // Act
        Func<Task> act = () => SaveAsync(store, Alice, "firmware.bbf", [1]);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>(
            "the endpoint checks this too, but the boundary has to hold whoever calls it");
    }

    [Fact]
    public async Task AFailedUploadLeavesNothingBehind()
    {
        // Arrange
        UserFileStore store = NewStore();

        // Act
        Func<Task> act = () => store.SaveAsync(Alice, "model.gcode", new ThrowingStream(), overwrite: false,
            CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<IOException>();
        store.List(Alice).Should().BeEmpty("a half-written upload must never be listable");
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Should().BeEmpty("nor left in the incoming directory");
    }

    [Fact]
    public async Task RenameMovesTheFileAndItsPrinterPath()
    {
        // Arrange
        UserFileStore store = NewStore();
        await SaveAsync(store, Alice, "old.gcode", Encoding.UTF8.GetBytes("content"));

        // Act
        StoredFile? renamed = store.Rename(Alice, "old.gcode", "new.gcode");

        // Assert
        renamed.Should().NotBeNull();
        renamed!.FileName.Should().Be("new.gcode");
        renamed.PrinterPath.Should().Be("/usb/new.gcode");
        (await File.ReadAllTextAsync(renamed.Path)).Should().Be("content", "a rename moves bytes, it does not copy them");
        store.List(Alice).Should().ContainSingle();
        store.Find(Alice, "old.gcode").Should().BeNull();
    }

    [Fact]
    public async Task RenameOntoAnExistingNameIsRefused()
    {
        // Arrange
        UserFileStore store = NewStore();
        await SaveAsync(store, Alice, "one.gcode", Encoding.UTF8.GetBytes("one"));
        await SaveAsync(store, Alice, "two.gcode", Encoding.UTF8.GetBytes("two"));

        // Act
        Action act = () => store.Rename(Alice, "one.gcode", "two.gcode");

        // Assert
        act.Should().Throw<PrintFileNameConflictException>();
        (await File.ReadAllTextAsync(store.Find(Alice, "two.gcode")!.Path)).Should().Be("two",
            "the file that was already there must be untouched");
    }

    [Fact]
    public async Task RenamingAFileToADifferentCaseOfItsOwnNameIsNotACollision()
    {
        // Arrange
        UserFileStore store = NewStore();
        await SaveAsync(store, Alice, "benchy.gcode", Encoding.UTF8.GetBytes("content"));

        // Act
        StoredFile? renamed = store.Rename(Alice, "benchy.gcode", "Benchy.gcode");

        // Assert
        renamed.Should().NotBeNull("a file cannot collide with itself");
        store.List(Alice).Should().ContainSingle();
    }

    [Fact]
    public void RenamingSomethingThatIsNotThereIsAMissRatherThanAnError()
    {
        // Arrange
        UserFileStore store = NewStore();

        // Act
        StoredFile? renamed = store.Rename(Alice, "absent.gcode", "present.gcode");

        // Assert
        renamed.Should().BeNull();
    }

    [Fact]
    public void RenamingToAnExtensionNoPrinterAcceptsIsRefused()
    {
        // Arrange
        UserFileStore store = NewStore();

        // Act
        Action act = () => store.Rename(Alice, "model.gcode", "model.bbf");

        // Assert
        act.Should().Throw<ArgumentException>(
            "otherwise rename would be the way around the upload allowlist");
    }

    [Fact]
    public async Task DeleteRemovesTheFileAndReportsWhetherThereWasOne()
    {
        // Arrange
        UserFileStore store = NewStore();
        await SaveAsync(store, Alice, "model.gcode", [1, 2, 3]);

        // Act
        bool deleted = store.Delete(Alice, "model.gcode");
        bool again = store.Delete(Alice, "model.gcode");

        // Assert
        deleted.Should().BeTrue();
        again.Should().BeFalse("deleting what is not there is an ordinary miss, not an error");
        store.List(Alice).Should().BeEmpty();
    }

    [Fact]
    public async Task ListReturnsOnlyTheCallersFilesWithTheirSizes()
    {
        // Arrange
        UserFileStore store = NewStore();
        await SaveAsync(store, Alice, "b.gcode", [1, 2]);
        await SaveAsync(store, Alice, "a.gcode", [1, 2, 3]);
        await SaveAsync(store, Bob, "c.gcode", [1]);

        // Act
        IReadOnlyList<StoredFile> files = store.List(Alice);

        // Assert
        files.Select(file => file.FileName).Should().Equal("a.gcode", "b.gcode");
        files[0].Length.Should().Be(3);
        files[1].Length.Should().Be(2);
    }

    [Fact]
    public void ListingBeforeAnythingIsUploadedIsEmptyRatherThanAnError()
    {
        // Arrange
        UserFileStore store = NewStore();

        // Act
        IReadOnlyList<StoredFile> files = store.List(Alice);

        // Assert
        files.Should().BeEmpty("a user who has uploaded nothing has no directory yet");
    }

    /// <summary>
    /// The allowlist is <b>narrower</b> than what the printer will accept, and that gap is a security
    /// control rather than a convenience.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An earlier version of this comment said the list "mirrors what the printer will accept ... so
    /// anything else would upload and transfer only to be refused at the far end". <b>That is false,
    /// and false in the dangerous direction.</b> Firmware gates a Connect transfer on
    /// <c>filename_is_transferrable</c>, which is
    /// <c>filename_is_printable || filename_is_firmware</c> - and <c>filename_is_firmware</c> is
    /// <c>.bbf</c>. So the printer accepts a <b>firmware image</b> as a transfer destination.
    /// </para>
    /// <para>
    /// This allowlist is therefore the only thing preventing an authenticated user from uploading a
    /// firmware image and pushing it to a printer. <b>Do not widen it on the grounds that the printer
    /// is less strict than we assumed</b> - that is true, and it is precisely the reason the
    /// narrowing exists.
    /// </para>
    /// <para>
    /// Note the asymmetry, which runs the opposite way to intuition: the firmware's own HTTP upload
    /// (<c>GcodeUpload::check_filename</c>) gates on <c>filename_is_printable</c> and refuses firmware
    /// with <c>415</c>. The <i>remote</i> path is the permissive one, so on the path that matters
    /// there is nothing behind this list.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("model.gcode", true)]
    [InlineData("model.bgcode", true)]
    [InlineData("MODEL.BGCODE", true)]
    [InlineData("model.gco", true)]
    [InlineData("model.bgc", true)]
    [InlineData("model.txt", false)]
    [InlineData("model.gcode.exe", false)]
    [InlineData("model", false)]

    // The one that matters: firmware images are transferrable as far as the printer is concerned, so
    // this case is the security property stated as an assertion rather than left as an absence.
    [InlineData("firmware.bbf", false)]
    [InlineData("FIRMWARE.BBF", false)]
    public void OnlyPrinterAcceptableExtensionsAreAllowed(string name, bool allowed)
    {
        UserFileStore.IsAllowedExtension(name).Should().Be(allowed);
    }

    private static Task<StoredFile> SaveAsync(UserFileStore store, long userId, string fileName, byte[] content,
        bool overwrite = false)
    {
        return store.SaveAsync(userId, fileName, new MemoryStream(content), overwrite, CancellationToken.None);
    }

    /// <summary>
    /// The directory carries the user's name beside their id, which is the whole reason the layout
    /// changed - <c>ls</c> in the data directory should say whose files these are.
    /// </summary>
    [Fact]
    public async Task AFileLandsInADirectoryNamedForItsOwner()
    {
        // Arrange
        UserFileStore store = NewStore();

        // Act
        await store.SaveAsync(Alice, "model.gcode", new MemoryStream([1, 2, 3]), overwrite: false,
            CancellationToken.None, "Sørensen");

        // Assert
        Directory.EnumerateDirectories(_root).Select(Path.GetFileName)
                 .Should().ContainSingle(name => name == "1-Sørensen");
    }

    /// <summary>
    /// <b>A user who changes their display name keeps one directory.</b> This is the property the
    /// whole prefix-glob design exists to hold, and the way to break it is to build the directory
    /// name from the current display name on every save instead of resolving first.
    /// </summary>
    /// <remarks>
    /// The failure it guards against is quiet rather than loud: two directories, each holding half of
    /// someone's files, with listings showing whichever the glob happened to reach first.
    /// </remarks>
    [Fact]
    public async Task ARenamedUserKeepsOneDirectoryAndAllTheirFiles()
    {
        // Arrange
        UserFileStore store = NewStore();

        await store.SaveAsync(Alice, "first.gcode", new MemoryStream([1]), overwrite: false,
            CancellationToken.None, "henrik");

        // Act - the same user, now displaying under a different name.
        await store.SaveAsync(Alice, "second.gcode", new MemoryStream([2]), overwrite: false,
            CancellationToken.None, "Henrik Sørensen");

        // Assert
        Directory.EnumerateDirectories(_root).Where(d => Path.GetFileName(d) != ".incoming")
                 .Should().ContainSingle("a rename must not split a user's files across two folders");

        store.List(Alice).Select(file => file.FileName)
             .Should().BeEquivalentTo(["first.gcode", "second.gcode"]);
    }

    /// <summary>
    /// A directory whose name is stale, or has no name at all, still resolves - lookup reads the id
    /// prefix and nothing else.
    /// </summary>
    [Theory]
    [InlineData("1-whoever-they-used-to-be")]
    [InlineData("1-Ægir")]
    public async Task ADirectoryResolvesByItsIdPrefixWhateverTheNameSays(string existingDirectory)
    {
        // Arrange - a directory already on disk, as an earlier save would have left it.
        UserFileStore store = NewStore();

        Directory.CreateDirectory(Path.Combine(_root, existingDirectory));

        // Act
        await store.SaveAsync(Alice, "model.gcode", new MemoryStream([1]), overwrite: false,
            CancellationToken.None, "Something Else Entirely");

        // Assert
        store.Find(Alice, "model.gcode").Should().NotBeNull();

        File.Exists(Path.Combine(_root, existingDirectory, "model.gcode"))
            .Should().BeTrue("the existing directory is the one that must have been used");
    }

    /// <summary>Another user's prefix must not be reachable through the glob.</summary>
    [Fact]
    public async Task AnIdIsNotAPrefixOfAnotherId()
    {
        // Arrange
        UserFileStore store = NewStore();

        // 1 and 12: without the hyphen in the pattern, "1-*" would claim "12-*" too.
        await store.SaveAsync(Alice, "alice.gcode", new MemoryStream([1]), overwrite: false,
            CancellationToken.None, "alice");
        await store.SaveAsync(12, "twelve.gcode", new MemoryStream([2]), overwrite: false,
            CancellationToken.None, "twelve");

        // Act & Assert
        store.List(Alice).Should().ContainSingle(file => file.FileName == "alice.gcode");
        store.List(12).Should().ContainSingle(file => file.FileName == "twelve.gcode");
    }

    private UserFileStore NewStore()
    {
        return new(Options.Create(new PrintFileStorageOptions { Directory = _root }),
            new HostEnvironmentAccessor(_root),
            TimeProvider.System,
            NullLogger<UserFileStore>.Instance);
    }

    /// <summary>A body that dies part-way through, which is what a disconnecting client looks like.</summary>
    private sealed class ThrowingStream : Stream
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

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new IOException("The client went away.");
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
