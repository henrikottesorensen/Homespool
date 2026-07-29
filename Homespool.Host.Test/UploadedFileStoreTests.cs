using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect.Transfers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="UploadedFileStore"/> - where uploaded gcode lands, and what it refuses.
/// </summary>
/// <remarks>
/// Both of its inputs reach the filesystem: a caller-supplied file name, and an id that becomes a
/// directory name. So most of these are about what <i>cannot</i> happen rather than what can - the
/// analyzer suppression on that class asserts these constraints hold, and this is where that claim
/// is actually tested.
/// </remarks>
public sealed class UploadedFileStoreTests : IDisposable
{
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
        UploadedFileStore store = NewStore();
        byte[] content = Encoding.UTF8.GetBytes("G28 ; home\nG1 X10\n");

        // Act
        StoredFile saved = await store.SaveAsync("model.bgcode", new MemoryStream(content), CancellationToken.None);
        StoredFile? found = store.Find(saved.Id);

        // Assert
        found.Should().NotBeNull();
        found!.Id.Should().Be(saved.Id);
        saved.Id.Should().HaveLength(27, "the id is Connect-shaped: 20 bytes base64url'd, and inside firmware's 28-character hash buffer");
        found.FileName.Should().Be("model.bgcode", "the name survives, because it is also the name the file takes on the printer");
        found.Length.Should().Be(content.Length);
        (await File.ReadAllBytesAsync(found.Path)).Should().Equal(content);
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
        UploadedFileStore store = NewStore();

        // Act
        StoredFile saved = await store.SaveAsync(given, new MemoryStream([1, 2, 3]), CancellationToken.None);

        // Assert
        saved.FileName.Should().Be(expected);
        Path.GetFullPath(saved.Path).Should().StartWith(Path.GetFullPath(_root),
            "nothing a caller supplies may place a file outside the store");
    }

    [Fact]
    public async Task ANameThatIsOnlyADirectoryIsRejected()
    {
        // Arrange
        UploadedFileStore store = NewStore();

        // Act
        Func<Task> act = () => store.SaveAsync("some/directory/", new MemoryStream([1]), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// The hash is concatenated into a path, so it has to be incapable of expressing one. It is 27
    /// base64url characters - an alphabet with no separator and no dot - and anything else is refused
    /// before it reaches the filesystem.
    /// </summary>
    [Theory]
    [InlineData("../../etc")]
    [InlineData("..")]
    [InlineData("/etc/passwd")]
    [InlineData("short")]
    [InlineData("../../../../etc/passwd....")] // right length, wrong alphabet
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaa")] // 26 - one short
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 28 - one long
    public void AHashThatIsNotTwentySevenBase64UrlCharactersIsRefused(string id)
    {
        // Arrange
        UploadedFileStore store = NewStore();

        // Act
        StoredFile? found = store.Find(id);

        // Assert
        found.Should().BeNull();
    }

    [Fact]
    public void AnUnknownButWellFormedHashIsSimplyNotFound()
    {
        // Arrange
        UploadedFileStore store = NewStore();

        // Act
        StoredFile? found = store.Find(new string('a', 27));

        // Assert
        found.Should().BeNull("an id that was never issued is an ordinary miss, not an error");
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
    /// firmware image and pushing it to a printer through <c>command/start/cloud</c>. <b>Do not widen
    /// it on the grounds that the printer is less strict than we assumed</b> - that is true, and it is
    /// precisely the reason the narrowing exists.
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
    public void OnlyPrinterAcceptableExtensionsAreAllowed(string name, bool allowed) =>
        UploadedFileStore.IsAllowedExtension(name).Should().Be(allowed);

    private UploadedFileStore NewStore() =>
        new(Options.Create(new FileStorageOptions { Directory = _root }),
            new HostEnvironmentAccessor(_root),
            NullLogger<UploadedFileStore>.Instance);
}
