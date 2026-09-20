using System;
using System.IO;
using System.Text;

using AwesomeAssertions;

namespace Homespool.Host.Test;

/// <summary>
/// The writer every key and secret file goes through: that the mode is on the file from the moment
/// it exists, and that a file already there is narrowed rather than left as it was found.
/// </summary>
public class RestrictedFileTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode WorldReadable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private readonly string _directory;

    public RestrictedFileTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "homespool-restricted-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A new file carries the mode without a separate step, whatever the process umask is - which in
    /// the runtime image is 0022, and so would otherwise leave the file world-readable.
    /// </summary>
    [Fact]
    public void ANewFileIsCreatedWithItsModeRatherThanNarrowedAfterwards()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string path = Path.Combine(_directory, "new.key");

        RestrictedFile.Write(path, "secret"u8.ToArray(), OwnerOnly);

        File.GetUnixFileMode(path).Should().Be(OwnerOnly);
    }

    /// <summary>
    /// A file that is already there is narrowed too, which the creation mode alone does not do.
    /// </summary>
    /// <remarks>
    /// This is the case that makes the explicit set after the write load-bearing rather than
    /// duplication: <c>UnixCreateMode</c> applies to a creation and is ignored for a file that
    /// exists, and every caller has a path that overwrites - a re-minted certificate, or a fixed
    /// temporary name an interrupted run left behind.
    /// </remarks>
    [Fact]
    public void AFileAlreadyThereIsNarrowedRatherThanKeepingTheModeItHad()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string path = Path.Combine(_directory, "leftover.key");
        File.WriteAllText(path, "what the interrupted run got as far as");
        File.SetUnixFileMode(path, WorldReadable);

        RestrictedFile.Write(path, "secret"u8.ToArray(), OwnerOnly);

        File.GetUnixFileMode(path).Should().Be(OwnerOnly);
    }

    /// <summary>
    /// The overwrite truncates, so nothing of a longer previous file is left past the new contents.
    /// </summary>
    [Fact]
    public void AnOverwriteReplacesTheContentsRatherThanWritingOverThem()
    {
        string path = Path.Combine(_directory, "truncated.json");
        File.WriteAllText(path, "a previous write that was considerably longer than the new one");

        RestrictedFile.Write(path, "{}", OwnerOnly);

        File.ReadAllText(path).Should().Be("{}");
    }

    /// <summary>
    /// Text is written as UTF-8 with no byte order mark, which is what a configuration layer reading
    /// the file back expects and what <c>File.WriteAllText</c> would have produced.
    /// </summary>
    [Fact]
    public void TextIsWrittenAsUtf8WithNoByteOrderMark()
    {
        string path = Path.Combine(_directory, "text.json");

        RestrictedFile.Write(path, "{\"Smtp\":\"post.kø.example\"}", OwnerOnly);

        File.ReadAllBytes(path)
            .Should()
            .Equal(Encoding.UTF8.GetBytes("{\"Smtp\":\"post.kø.example\"}"));
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
