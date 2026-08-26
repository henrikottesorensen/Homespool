using System;

using AwesomeAssertions;

namespace Homespool.Host.Test;

/// <summary>
/// Reading the build stamp the SDK writes into every assembly at compile time.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two "unknown" cases are the ones worth having.</b> A stamp with a commit in it is what
/// every ordinary build produces, so it is checked here mostly to pin the format; the branches that
/// matter are the ones nobody intends. A container built without the commit passed in produces a
/// version with no build metadata at all - and it gets there through a SILENT omission, an MSBuild
/// condition rather than a warning - so this output is the only place that break can be noticed.
/// A bare version number would hide it.
/// </para>
/// <para>
/// <b>What these tests do not cover, stated so nobody reads more into a green run:</b> the argument
/// wiring in <c>Program.Main</c>. Reaching it means running <c>Main</c>, and neither this project nor
/// <c>Homespool.Host.E2ETest</c> has a seam for that - the same gap the <c>--iana-timezone</c> applet
/// beside it has always had. What is pinned is the parsing and the wording; that <c>--version</c> is
/// wired to it is checked by running the binary.
/// </para>
/// </remarks>
public class BuildInformationTests
{
    private const string Commit = "dd029e0fa7a6a29b699dc1d7f3b414ae2903ee38";

    [Fact]
    public void AVersionCarryingACommitReportsBoth()
    {
        string described = BuildInformation.Describe("Homespool", $"0.0.1+{Commit}");

        described.Should().Be($"Homespool 0.0.1{Environment.NewLine}commit {Commit}");
    }

    [Fact]
    public void AVersionCarryingAModifiedCommitSaysSo()
    {
        string described = BuildInformation.Describe("Homespool", $"0.0.1+{Commit}.dirty");

        described.Should().Be($"Homespool 0.0.1{Environment.NewLine}commit {Commit} (modified)");
    }

    /// <summary>
    /// The marker must not survive into the commit itself, or the reported sha matches nothing.
    /// </summary>
    /// <remarks>
    /// Worth its own case rather than being left to the assertion above: pasting the printed value
    /// into <c>git show</c> is the entire purpose, and a commit with a suffix stuck on the end fails
    /// that while still looking correct in a diff of the output.
    /// </remarks>
    [Fact]
    public void TheModifiedMarkerIsNotPartOfTheReportedCommit()
    {
        string described = BuildInformation.Describe("Homespool", $"0.0.1+{Commit}.dirty");

        described.Should().Contain(Commit + " ");
        described.Should().NotContain(".dirty");
    }

    /// <summary>A container image built without the commit passed in.</summary>
    [Fact]
    public void AVersionWithNoBuildMetadataSaysTheCommitIsUnknownAndWhy()
    {
        string described = BuildInformation.Describe("Homespool", "0.0.1");

        described.Should()
                 .Be($"Homespool 0.0.1{Environment.NewLine}"
                   + "commit unknown - built with no source control information");
    }

    /// <summary>An assembly carrying no informational version attribute at all.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentStampIsReportedRatherThanCrashing(string? informationalVersion)
    {
        string described = BuildInformation.Describe("Homespool", informationalVersion);

        described.Should()
                 .Be($"Homespool (version unknown){Environment.NewLine}"
                   + "commit unknown - this assembly carries no version information");
    }

    /// <summary>
    /// The SDK appends with a dot when the version already carries a <c>+</c>, so a stamp can hold
    /// more than one metadata segment.
    /// </summary>
    /// <remarks>
    /// Splitting on the FIRST <c>+</c> and taking the rest verbatim is what keeps such a stamp
    /// readable rather than truncated at the wrong place. No build path here produces one today; this
    /// pins the parse against the SDK behaviour that would create it.
    /// </remarks>
    [Fact]
    public void MetadataBeyondTheCommitIsKeptRatherThanTruncated()
    {
        string described = BuildInformation.Describe("Homespool", $"0.0.1-beta.1+build7.{Commit}");

        described.Should().Be($"Homespool 0.0.1-beta.1{Environment.NewLine}commit build7.{Commit}");
    }

    /// <summary>
    /// This binary's own stamp, whatever it happens to be, must render without throwing.
    /// </summary>
    /// <remarks>
    /// Deliberately asserts nothing about the value: the test suite runs on a laptop, in a container
    /// and in CI, and the commit differs in each. What it does catch is a real class of failure - a
    /// stamp shape nobody anticipated reaching the parser as an exception on a machine where nobody
    /// ran <c>--version</c> by hand.
    /// </remarks>
    [Fact]
    public void ThisAssemblysOwnStampRenders()
    {
        string described = BuildInformation.Describe("Homespool", BuildInformation.ReadInformationalVersion());

        described.Should().StartWith("Homespool ");
        described.Should().Contain("commit ");
    }

    /// <summary>
    /// The footer's one-line form, which is what a signed-in person actually reads.
    /// </summary>
    /// <remarks>
    /// The commit is abbreviated here and whole in <c>--version</c>, so these cases pin the length
    /// as well as the shape - a footer that printed forty characters would push the copyright line
    /// off a narrow screen, which is the reason for the abbreviation rather than taste.
    /// </remarks>
    [Fact]
    public void TheFooterFormAbbreviatesTheCommit()
    {
        BuildInformation.Summarise($"0.0.1+{Commit}").Should().Be("0.0.1 (dd029e0)");
    }

    [Fact]
    public void TheFooterFormSaysWhenTheTreeWasModified()
    {
        BuildInformation.Summarise($"0.0.1+{Commit}.dirty").Should().Be("0.0.1 (dd029e0, modified)");
    }

    /// <summary>
    /// A commit shorter than the abbreviation must not be sliced past its end.
    /// </summary>
    [Fact]
    public void AShortCommitIsNotTruncatedPastItsEnd()
    {
        BuildInformation.Summarise("0.0.1+abc").Should().Be("0.0.1 (abc)");
    }

    /// <summary>
    /// Where the footer deliberately differs from <c>--version</c>: no commit means no brackets,
    /// rather than the word "unknown" on every page of the deployment.
    /// </summary>
    [Fact]
    public void TheFooterFormOmitsAnUnknownCommitRatherThanLabellingIt()
    {
        BuildInformation.Summarise("0.0.1").Should().Be("0.0.1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TheFooterFormIsEmptyWhenThereIsNoStampAtAll(string? informationalVersion)
    {
        BuildInformation.Summarise(informationalVersion).Should().BeEmpty();
    }
}
