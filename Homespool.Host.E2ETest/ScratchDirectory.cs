using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Homespool.Host.E2ETest;

/// <summary>
/// One directory per test, holding everything that test writes: its database, its content root, and
/// whatever a component puts under either.
/// </summary>
/// <remarks>
/// <para>
/// <b>The point is that cleanup deletes a directory rather than a list of file names.</b> Each test
/// used to own two unrelated temp paths and remove the database by naming it three times - the file,
/// then <c>-wal</c>, then <c>-shm</c>. Seven of the forty-eight classes named only the first, which
/// nothing noticed: on a disk-backed temp directory the leftovers are litter, and the suite is green
/// either way. On a machine where <c>/tmp</c> is tmpfs they are resident memory that outlives the run
/// that made them, and enough runs exhaust it. A recursive delete cannot forget a suffix, so the whole
/// class of mistake goes away instead of being corrected in seven places.
/// </para>
/// <para>
/// <b>The sweep runs at the start of a run, not the end, and that is the load-bearing half.</b> The
/// leftovers that accumulate are precisely the ones no cleanup could have removed - a killed run, an
/// OOM, a crashed host - because <c>Dispose</c> and <c>finally</c> both need the process to still be
/// alive. Nothing a run does at its own exit can help there. What works is cleaning up after the
/// <i>previous</i> run, which is what this does.
/// </para>
/// <para>
/// Every run's directories sit under one <see cref="RunRootPrefix"/>-named root, so a human looking at
/// a temp directory sees one thing that is obviously this suite's, rather than the fourteen unrelated
/// name shapes this replaced.
/// </para>
/// </remarks>
public sealed class ScratchDirectory : IDisposable
{
    /// <summary>What every run root is named, so the sweep and a person can both recognise one.</summary>
    private const string RunRootPrefix = "homespool-e2e-";

    /// <summary>
    /// How stale another run's root must be before this one deletes it.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. The only thing it has to beat is a concurrently running suite - two
    /// checkouts testing at once, which happens here - and deleting a live run's databases would be a
    /// far worse failure than leaving litter for an afternoon. Nothing needs this to be prompt: the
    /// cost of a stale root is disk until the next run notices it.
    /// </remarks>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    private static readonly Lazy<string> RunRoot = new(PrepareRunRoot);

    /// <summary>
    /// Every scratch directory made and not yet removed, so the end of the run can say whether any test
    /// failed to clean up after itself. See <see cref="ScratchLeakDetector"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> Live = new();

    /// <summary>The scratch directories still outstanding, newest name first. Empty when every test tidied up.</summary>
    public static IReadOnlyCollection<string> Outstanding => [.. Live.Values];

    private ScratchDirectory(string path)
    {
        Path = path;
    }

    /// <summary>The directory this test owns. Everything it writes belongs under here.</summary>
    public string Path { get; }

    /// <summary>The connection string for this test's own database, inside its own directory.</summary>
    public string ConnectionString =>
        $"Data Source={System.IO.Path.Combine(Path, "test.db")}";

    /// <summary>
    /// Makes a directory for one test, under this run's root.
    /// </summary>
    /// <param name="name">
    /// A short name for the test that owns it, so a directory left behind by a crash says which test
    /// made it. Not required to be unique - a GUID is appended.
    /// </param>
    public static ScratchDirectory Create(string name)
    {
        string path = System.IO.Path.Combine(RunRoot.Value, $"{name}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path);

        Live[path] = name;

        return new ScratchDirectory(path);
    }

    /// <summary>Removes this test's directory and everything in it.</summary>
    public void Dispose()
    {
        Live.TryRemove(Path, out _);

        Discard(Path);
    }

    /// <summary>
    /// Creates this run's root, and removes the roots of runs that are long gone.
    /// </summary>
    private static string PrepareRunRoot()
    {
        string temp = System.IO.Path.GetTempPath();

        SweepStaleRuns(temp);

        string root = System.IO.Path.Combine(
            temp,
            RunRootPrefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                          + "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        Directory.CreateDirectory(root);

        return root;
    }

    /// <summary>
    /// Deletes other runs' roots once they are old enough that nothing can still be using them.
    /// </summary>
    /// <remarks>
    /// Best-effort throughout: a root belonging to another user, or one a concurrent run is deleting
    /// at the same moment, must not fail the suite. The consequence of skipping one is that it is
    /// swept next time.
    /// </remarks>
    private static void SweepStaleRuns(string temp)
    {
        DateTime cutoff = DateTime.UtcNow - StaleAfter;

        IEnumerable<string> roots;

        try
        {
            roots = Directory.EnumerateDirectories(temp, RunRootPrefix + "*");
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (string root in roots)
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(root) < cutoff)
                {
                    Discard(root);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void Discard(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A test's own directory going unremoved is litter, not a failure, and the next run's
            // sweep collects it. Failing here would turn a tidy-up into a red suite.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
