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
    /// The allowlist mirrors what the printer will accept - <c>filename_is_transferrable</c> gates the
    /// transfer and <c>filename_is_printable</c> the print - so anything else would upload and
    /// transfer only to be refused at the far end.
    /// </summary>
    [Theory]
    [InlineData("model.gcode", true)]
    [InlineData("model.bgcode", true)]
    [InlineData("MODEL.BGCODE", true)]
    [InlineData("model.gco", true)]
    [InlineData("model.bgc", true)]
    [InlineData("model.txt", false)]
    [InlineData("model.gcode.exe", false)]
    [InlineData("model", false)]
    public void OnlyPrinterAcceptableExtensionsAreAllowed(string name, bool allowed) =>
        UploadedFileStore.IsAllowedExtension(name).Should().Be(allowed);

    private UploadedFileStore NewStore() =>
        new(Options.Create(new FileStorageOptions { Directory = _root }),
            new HostEnvironmentAccessor(_root),
            NullLogger<UploadedFileStore>.Instance);
}
