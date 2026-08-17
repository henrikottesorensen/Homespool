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
/// is built, so there is no request culture and no localiser yet; it sits on the same side of
/// <c>notes/localisation.md</c>'s boundary as log lines.
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
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return $"{product} (version unknown){Environment.NewLine}"
                 + "commit unknown - this assembly carries no version information";
        }

        int metadataStart = informationalVersion.IndexOf('+');

        if (metadataStart < 0)
        {
            // A container image built with no gitref passed in lands here, and it is the reason this
            // branch explains itself instead of printing the version alone: the build succeeded, so
            // the only place the broken chain can be noticed is right here.
            return $"{product} {informationalVersion}{Environment.NewLine}"
                 + "commit unknown - built with no source control information";
        }

        string version = informationalVersion[..metadataStart];
        string metadata = informationalVersion[(metadataStart + 1)..];
        bool modified = metadata.EndsWith(ModifiedMarker, StringComparison.Ordinal);
        string commit = modified ? metadata[..^ModifiedMarker.Length] : metadata;

        return $"{product} {version}{Environment.NewLine}"
             + $"commit {commit}{(modified ? " (modified)" : string.Empty)}";
    }
}
