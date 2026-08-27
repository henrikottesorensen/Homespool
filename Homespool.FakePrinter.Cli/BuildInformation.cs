using System;
using System.Reflection;

namespace Homespool.FakePrinter.Cli;

/// <summary>
/// Which build this rig is: the version it was stamped with and the commit it was built from.
/// </summary>
/// <remarks>
/// <para>
/// <b>A deliberate copy of <c>Homespool.Host/BuildInformation.cs</c>, and it stays a copy.</b> The
/// FakePrinter references neither Homespool.Host nor Homespool.Model, so that where the fake and the
/// server disagree about the wire the disagreement is a test result rather than a shared assumption.
/// Sharing eight lines of attribute reading is not worth the
/// first hole in that; the full reasoning for the mechanism lives beside the other copy rather than
/// being restated here, where it would drift.
/// </para>
/// <para>
/// <b>Why a load rig reports its own build at all.</b> Its output is numbers that get written into
/// notes and compared across weeks, and a measurement is worth nothing if nobody can say which fake
/// produced it.
/// </para>
/// </remarks>
public static class BuildInformation
{
    /// <summary>The argument that reports which build this is and exits without connecting.</summary>
    public const string VersionArgument = "--version";

    /// <summary>What a build path appends to the commit when built from a modified working tree.</summary>
    private const string ModifiedMarker = ".dirty";

    /// <summary>Writes the build description to standard output.</summary>
    /// <param name="product">The name to print, as a person would say it.</param>
    /// <returns>Zero. An absent stamp is reported, not an error.</returns>
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

    /// <summary>Renders a stamp as the two lines <c>--version</c> prints.</summary>
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
