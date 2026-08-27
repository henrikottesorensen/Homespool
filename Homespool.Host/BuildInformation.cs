using System;
using System.Reflection;

namespace Homespool.Host;

/// <summary>
/// Which build this binary is: the version it was stamped with and the commit it was built from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here writes the stamp - the SDK already did it.</b> SourceLink ships inside the SDK
/// and resolves <c>SourceRevisionId</c> from the repository during compilation, and
/// <c>AddSourceRevisionToInformationalVersion</c> appends it to
/// <see cref="AssemblyInformationalVersionAttribute"/> as <c>0.0.1+&lt;sha&gt;</c>. So this is a
/// reader over something every local build has always produced, and the only work is parsing it.
/// </para>
/// <para>
/// <b>The one place that is not true is a container build</b>, because <c>.dockerignore</c> excludes
/// <c>**/.git</c>: with no repository to read, <c>SourceRevisionId</c> is empty and the SDK's append
/// is skipped by a condition, silently, leaving the deployed artefact as the one build that cannot
/// say what it is. The Dockerfile passes the value in through a <c>SourceRevisionId</c> environment
/// variable instead - MSBuild reads environment variables as properties - and <c>build.sh</c>,
/// <c>pi/build.sh</c> and CI supply it from <c>tools/gitref.sh</c>. That is why "unknown" below is a
/// sentence rather than a blank: it means the chain broke, and it should read like it.
/// </para>
/// <para>
/// <b>The modified marker is only ever applied by those build paths</b>, never at compile time.
/// Asking git whether the working tree differs from <c>HEAD</c> answers a different question than
/// "could this binary differ from what <c>HEAD</c> would build" - a fifth of this repository's
/// commits touch no compile input at all - so a compile-time check would report a README or
/// <c>setup-env.sh</c> edit as a modified binary. The shell paths above produce a distributable
/// artefact, where any difference from <c>HEAD</c> is worth flagging, so the crude check is the
/// correct one there and absent here.
/// </para>
/// <para>
/// <b>Not localised, deliberately.</b> This is operator-facing machine text printed before the host
/// is built, so there is no request culture and no localiser yet; it sits on the same side of that
/// boundary as log lines.
/// </para>
/// </remarks>
public static class BuildInformation
{
    /// <summary>The argument that reports which build this is and exits without starting a server.</summary>
    public const string VersionArgument = "--version";

    /// <summary>
    /// What a build path appends to the commit when it was built from a modified working tree.
    /// </summary>
    /// <remarks>
    /// A dot rather than a hyphen, following the SDK's own convention for a second build-metadata
    /// segment. <c>+sha.dirty</c> stays build metadata; <c>+sha-dirty</c> would read as a
    /// pre-release tag.
    /// </remarks>
    private const string ModifiedMarker = ".dirty";

    /// <summary>
    /// How much of the commit the footer shows, matching git's own default abbreviation.
    /// </summary>
    private const int ShortCommitLength = 7;

    /// <summary>
    /// This build, as the footer shows it — <c>0.0.1 (dd029e0)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Computed once rather than per request: the footer is on every page, and this is reflection
    /// over an assembly attribute that cannot change while the process runs.
    /// </para>
    /// <para>
    /// <b>Shown only to signed-in users</b> (`_Layout.cshtml`), which is a deliberate narrowing
    /// rather than an accident of where it was convenient to put it. The layout renders on the
    /// anonymous sign-in and registration pages too, and an exact commit on a public page names
    /// precisely which known defects apply to the deployment — the same reasoning that keeps the
    /// build off <c>/health</c>. The version alone would have been defensible in public; the commit
    /// is not.
    /// </para>
    /// </remarks>
    public static string Summary { get; } = Summarise(ReadInformationalVersion());

    /// <summary>Writes the build description to standard output.</summary>
    /// <param name="product">The name to print, as a person would say it.</param>
    /// <returns>Zero. There is no failure case - an absent stamp is reported, not an error.</returns>
    public static int WriteVersion(string product)
    {
        Console.WriteLine(Describe(product, ReadInformationalVersion()));

        return 0;
    }

    /// <summary>Reads the informational version this binary was compiled with.</summary>
    /// <returns>The stamp, or <see langword="null"/> when the assembly carries none.</returns>
    public static string? ReadInformationalVersion()
    {
        return Assembly.GetEntryAssembly()
                       ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                       ?.InformationalVersion;
    }

    /// <summary>
    /// Renders a stamp as the two lines <c>--version</c> prints: the version, then the commit.
    /// </summary>
    /// <remarks>
    /// Separated from reading the attribute so the parsing can be tested against the shapes that
    /// actually occur - with a commit, with a modified commit, with no build metadata at all
    /// (a container built without the ref), and with no attribute (a trimmed or hand-built
    /// assembly). Only the first two are anybody's intent; the other two are the ones worth being
    /// legible about.
    /// </remarks>
    /// <param name="product">The name to print.</param>
    /// <param name="informationalVersion">The stamp, or <see langword="null"/> when there is none.</param>
    /// <returns>Two lines, without a trailing newline.</returns>
    public static string Describe(string product, string? informationalVersion)
    {
        (string? version, string? commit, bool modified) = Parse(informationalVersion);

        if (version is null)
        {
            return $"{product} (version unknown){Environment.NewLine}"
                 + "commit unknown - this assembly carries no version information";
        }

        if (commit is null)
        {
            // A container image built with no gitref passed in lands here, and it is the reason this
            // branch explains itself instead of printing the version alone: the build succeeded, so
            // the only place the broken chain can be noticed is right here.
            return $"{product} {version}{Environment.NewLine}"
                 + "commit unknown - built with no source control information";
        }

        return $"{product} {version}{Environment.NewLine}"
             + $"commit {commit}{(modified ? " (modified)" : string.Empty)}";
    }

    /// <summary>
    /// Renders a stamp as the one line the footer shows — <c>0.0.1 (dd029e0)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The commit is abbreviated to <see cref="ShortCommitLength"/> because this is read by a person
    /// glancing at a page, not pasted into a report. It is still enough to find the commit, and
    /// <c>--version</c> remains the place that gives the whole thing.
    /// </para>
    /// <para>
    /// <b>The commit is omitted entirely rather than replaced by a placeholder when it is unknown.</b>
    /// That is the opposite of <see cref="Describe"/>, deliberately: the argument's whole job is to
    /// answer "which build is this", so a missing answer there is the finding and has to be stated.
    /// A footer is decoration on a page somebody opened for another reason, and "unknown" in it would
    /// be noise on every page of every deployment built from an archive.
    /// </para>
    /// </remarks>
    /// <param name="informationalVersion">The stamp, or <see langword="null"/> when there is none.</param>
    /// <returns>The version, with the short commit in brackets when there is one.</returns>
    public static string Summarise(string? informationalVersion)
    {
        (string? version, string? commit, bool modified) = Parse(informationalVersion);

        if (version is null)
        {
            return string.Empty;
        }

        if (commit is null)
        {
            return version;
        }

        string shortCommit = commit.Length > ShortCommitLength ? commit[..ShortCommitLength] : commit;

        return modified ? $"{version} ({shortCommit}, modified)" : $"{version} ({shortCommit})";
    }

    /// <summary>Splits a stamp into the three things anything here wants to know.</summary>
    /// <remarks>
    /// One parse behind both renderings, so the footer and <c>--version</c> cannot come to different
    /// conclusions about the same assembly — which they could while each split the string itself.
    /// </remarks>
    /// <param name="informationalVersion">The stamp, or <see langword="null"/> when there is none.</param>
    /// <returns>
    /// The version (<see langword="null"/> when the assembly carries no stamp at all), the commit
    /// (<see langword="null"/> when the stamp carries no build metadata), and whether the build path
    /// marked it as coming from a modified working tree.
    /// </returns>
    private static (string? version, string? commit, bool modified) Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return (null, null, false);
        }

        int metadataStart = informationalVersion.IndexOf('+');

        if (metadataStart < 0)
        {
            return (informationalVersion, null, false);
        }

        string metadata = informationalVersion[(metadataStart + 1)..];
        bool modified = metadata.EndsWith(ModifiedMarker, StringComparison.Ordinal);

        return (
            informationalVersion[..metadataStart],
            modified ? metadata[..^ModifiedMarker.Length] : metadata,
            modified);
    }
}
