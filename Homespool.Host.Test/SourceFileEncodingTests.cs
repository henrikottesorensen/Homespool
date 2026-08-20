using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using AwesomeAssertions;

namespace Homespool.Host.Test;

/// <summary>
/// Every source file this repository owns is UTF-8, and carries no byte order mark.
/// </summary>
/// <remarks>
/// <para>
/// A test rather than an analyzer because nothing else can enforce it. StyleCop's SA1412 only
/// asserts the <i>opposite</i> (BOM required) and ships no inverse; <c>.editorconfig</c>'s
/// <c>charset = utf-8</c> is honoured by editors but invisible to the build; and git cannot help -
/// <c>working-tree-encoding=UTF-8</c> was tried and is a no-op, because with UTF-8 declared there is
/// nothing to re-encode so nothing gets validated. There is no CI to hang a script off, so this runs
/// where everything else here runs.
/// </para>
/// <para>
/// The BOM half is a house rule: UTF-8 has no byte order, so the mark encodes nothing and breaks
/// tools that expect a file to begin with what it says it begins with - a shebang, a
/// <c>FROM</c> line, the first pattern in a <c>.dockerignore</c>. 36 files carried one when this was
/// written, all from tooling that ignores <c>.editorconfig</c>: the EF migration scaffolder and the
/// ASP.NET Identity page scaffolder. Regenerating either will reintroduce them, and this is what
/// says so.
/// </para>
/// <para>
/// The UTF-8 half guards something worse. A file that is <i>not</i> valid UTF-8 - an editor saving
/// as ANSI - is decoded to <c>U+FFFD</c> replacement characters silently, with no compiler error and
/// no warning. Measured on .NET 6, 8 and 10 SDKs, in Linux containers under both the default locale
/// and <c>LANG=C</c>: every one corrupts identically. A BOM would not have saved it either, so this
/// is the only guard there is.
/// </para>
/// </remarks>
public class SourceFileEncodingTests
{
    /// <summary>
    /// Directories holding files this repository does not own or does not hand-write. <c>lib</c> is
    /// LibMan's vendored client assets, and the last four are gitignored working directories that a
    /// filesystem walk would otherwise wander into.
    /// </summary>
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "obj", "bin", ".git", ".idea", ".vs", "node_modules", "lib",
        "firmware", "logs", "notes", "private-captures",
    };

    /// <summary>Recorded wire data, kept byte-exact - see <c>.gitattributes</c>.</summary>
    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".capture", ".cap", ".map",
    };

    private static readonly HashSet<string> CheckedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".cshtml", ".csproj", ".slnx", ".props", ".targets",
        ".json", ".jsonopenapi", ".yaml", ".yml",
        ".js", ".css", ".html", ".md", ".sh", ".txt", ".example",
    };

    /// <summary>Files worth checking that have no extension, or whose whole name is the extension.</summary>
    private static readonly HashSet<string> CheckedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", ".dockerignore", ".editorconfig", ".gitignore", ".gitattributes",
    };

    /// <summary>
    /// Walks up from the test assembly until the solution file appears. Throws rather than returning
    /// null: a test that cannot find the tree must fail loudly, not silently check nothing.
    /// </summary>
    private static DirectoryInfo RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Homespool.slnx")))
        {
            directory = directory.Parent;
        }

        return directory
               ?? throw new InvalidOperationException($"No Homespool.slnx above {AppContext.BaseDirectory}.");
    }

    private static IEnumerable<string> SourceFiles(DirectoryInfo directory)
    {
        foreach (FileInfo file in directory.EnumerateFiles())
        {
            bool checkIt = CheckedNames.Contains(file.Name)
                           || (CheckedExtensions.Contains(file.Extension) && !SkippedExtensions.Contains(file.Extension));

            if (checkIt && !file.Name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)
                        && !file.Name.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase))
            {
                yield return file.FullName;
            }
        }

        foreach (DirectoryInfo child in directory.EnumerateDirectories().Where(d => !SkippedDirectories.Contains(d.Name)))
        {
            foreach (string file in SourceFiles(child))
            {
                yield return file;
            }
        }
    }

    private static IReadOnlyList<string> AllSourceFiles()
    {
        return SourceFiles(RepositoryRoot()).ToList();
    }

    private static string Relative(string path)
    {
        return Path.GetRelativePath(RepositoryRoot().FullName, path);
    }

    /// <summary>
    /// The scan itself has to be known-good, or every assertion below passes vacuously the day the
    /// root lookup or the filters break.
    /// </summary>
    [Fact]
    public void TheScanActuallyFindsTheSourceTree()
    {
        IReadOnlyList<string> files = AllSourceFiles();

        files.Should().HaveCountGreaterThan(200,
                                            "the repository has 240+ C# files alone - a smaller number means the walk is not reaching the source tree");
        files.Should().Contain(f => Relative(f) == Path.Combine("Homespool.Host", "Program.cs"),
                               "a known file must be in scope, not merely some files");
    }

    [Fact]
    public void NoSourceFileStartsWithAByteOrderMark()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];

        List<string> offenders = AllSourceFiles()
                                 .Where(path =>
                                 {
                                     using FileStream stream = File.OpenRead(path);
                                     Span<byte> head = stackalloc byte[3];

                                     return stream.ReadAtLeast(head, 3, throwOnEndOfStream: false) == 3 && head.SequenceEqual(bom);
                                 })
                                 .Select(Relative)
                                 .Order()
                                 .ToList();

        offenders.Should().BeEmpty(
            "UTF-8 has no byte order to mark, and the signature breaks tools that read the first bytes of a file. "
            + "The EF and Identity scaffolders write one - strip it after regenerating. Offenders:\n"
            + string.Join('\n', offenders));
    }

    [Fact]
    public void EverySourceFileIsValidUtf8()
    {
        UTF8Encoding strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        List<string> offenders = AllSourceFiles()
                                 .Where(path =>
                                 {
                                     try
                                     {
                                         strict.GetString(File.ReadAllBytes(path));

                                         return false;
                                     }
                                     catch (DecoderFallbackException)
                                     {
                                         return true;
                                     }
                                 })
                                 .Select(Relative)
                                 .Order()
                                 .ToList();

        offenders.Should().BeEmpty(
            "a non-UTF-8 source file is decoded to U+FFFD silently - no compiler error, no warning, on every SDK "
            + "tested - so string literals change meaning with nothing to notice it. Offenders:\n"
            + string.Join('\n', offenders));
    }
}
