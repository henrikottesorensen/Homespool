using System;
using System.Globalization;
using System.IO;

using AwesomeAssertions;

using Homespool.Data;

namespace Homespool.Host.Test;

/// <summary>
/// The arguments that answer and exit rather than start a server, and the time-zone conversion
/// <c>setup-env.sh</c> asks for through them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shell suite stubs <c>dotnet</c> for this call</b>, so it proves how the script checks an
/// answer and nothing about the answer. These run the real conversion: that the region changes it,
/// that the empty region the script passes when none is set means no region, and that a zone the
/// framework does not know writes nothing and exits one - the two things the script reads to decide
/// whether to carry on detecting.
/// </para>
/// <para>
/// <b>Handled is asserted on the refusals too.</b> An applet that answered "no" by returning false
/// would hand the arguments on to <c>WebApplication.CreateBuilder</c>, and the server would start -
/// the older-image trap the script guards against, reproduced by a current one.
/// </para>
/// <para>
/// Standard output is captured by redirecting <see cref="Console.Out"/> around the call. Nothing else
/// in this project writes there - the schema writer and the migration guard write to standard error -
/// and the tests of one class run one at a time, so the capture sees only its own applet's line.
/// </para>
/// </remarks>
public sealed class StartupAppletsTests
{
    private const string IanaTimeZone = "--iana-timezone";

    /// <summary>The ordinary start: nothing to answer, the server runs.</summary>
    [Fact]
    public void NoArgumentsIsNotAnApplet()
    {
        (bool handled, int exitCode, string output) = Run();

        handled.Should().BeFalse("with nothing asked, the server starts");
        exitCode.Should().Be(0);
        output.Should().BeEmpty();
    }

    /// <summary>
    /// The arguments a server run carries - <c>--urls</c>, an ASP.NET Core switch - are none of ours.
    /// </summary>
    [Fact]
    public void AnArgumentNamingNoAppletIsLeftToTheServer()
    {
        (bool handled, _, string output) = Run("--urls", "http://localhost:5000");

        handled.Should().BeFalse("an argument this class does not know belongs to the host");
        output.Should().BeEmpty();
    }

    /// <summary>The conversion itself: what Windows calls the zone, written as what <c>TZ</c> takes.</summary>
    [Fact]
    public void AWindowsZoneIsWrittenAsItsIanaName()
    {
        (bool handled, int exitCode, string output) = Run(IanaTimeZone, "W. Europe Standard Time");

        handled.Should().BeTrue();
        exitCode.Should().Be(0);
        output.Should().Be("Europe/Berlin" + Environment.NewLine, "one line, nothing else, so the script can take the last line");
    }

    /// <summary>
    /// The region is the reason the applet takes a second argument: the same Windows zone covers
    /// Paris and Copenhagen, and without it a Dane's <c>.env</c> would say Paris.
    /// </summary>
    [Fact]
    public void ARegionPicksTheLocalNameForASharedZone()
    {
        (_, int exitCode, string output) = Run(IanaTimeZone, "Romance Standard Time", "DK");

        exitCode.Should().Be(0);
        output.Should().Be("Europe/Copenhagen" + Environment.NewLine);
    }

    /// <summary>Without a region, the zone's own default.</summary>
    [Fact]
    public void WithoutARegionASharedZoneGetsItsDefault()
    {
        (_, int exitCode, string output) = Run(IanaTimeZone, "Romance Standard Time");

        exitCode.Should().Be(0);
        output.Should().Be("Europe/Paris" + Environment.NewLine);
    }

    /// <summary>
    /// The script always passes three arguments - the region expands to an empty string when the
    /// launcher gave none - so an empty region must mean no region, not a region called nothing.
    /// </summary>
    /// <remarks>
    /// The applet passes the empty region straight to the framework, which reads it as none. This is
    /// what fails if a framework release stops doing that.
    /// </remarks>
    [Fact]
    public void TheEmptyRegionTheScriptPassesMeansNone()
    {
        (_, int exitCode, string output) = Run(IanaTimeZone, "Romance Standard Time", string.Empty);

        exitCode.Should().Be(0, "an empty region is the script's spelling of no region");
        output.Should().Be("Europe/Paris" + Environment.NewLine);
    }

    /// <summary>
    /// A region the zone does not cover does not lose the answer: the framework falls back to the
    /// zone's default, so a launcher reporting a region the zone has no name for still gets a zone.
    /// </summary>
    [Fact]
    public void ARegionTheZoneDoesNotCoverFallsBackToItsDefault()
    {
        (_, int exitCode, string output) = Run(IanaTimeZone, "Romance Standard Time", "JP");

        exitCode.Should().Be(0);
        output.Should().Be("Europe/Paris" + Environment.NewLine);
    }

    /// <summary>
    /// The refusal the script reads: nothing on standard output and a non-zero exit, so detection
    /// carries on to the clock. And still handled, or the server would start.
    /// </summary>
    [Fact]
    public void AnUnknownZoneWritesNothingAndExitsOne()
    {
        (bool handled, int exitCode, string output) = Run(IanaTimeZone, "Not A Zone");

        handled.Should().BeTrue("a refused conversion is still an applet run, not a server start");
        exitCode.Should().Be(1);
        output.Should().BeEmpty("the script takes any line as a candidate answer");
    }

    /// <summary>The argument alone, with no zone to convert, is the same refusal.</summary>
    [Fact]
    public void AMissingZoneWritesNothingAndExitsOne()
    {
        (bool handled, int exitCode, string output) = Run(IanaTimeZone);

        handled.Should().BeTrue();
        exitCode.Should().Be(1);
        output.Should().BeEmpty();
    }

    /// <summary>
    /// The wiring for <c>--version</c>: the wording is pinned in <see cref="BuildInformationTests"/>,
    /// this is that the argument reaches it and the server does not start.
    /// </summary>
    [Fact]
    public void TheVersionArgumentAnswersAndExits()
    {
        (bool handled, int exitCode, string output) = Run(BuildInformation.VersionArgument);

        handled.Should().BeTrue();
        exitCode.Should().Be(0);
        output.Should().StartWith("Homespool ", "the product name the applet is given");
    }

    /// <summary>
    /// The wiring for <c>--write-schema</c>: with no path it is the usage refusal, on standard error,
    /// which is enough to show the argument reached the writer without writing a database.
    /// </summary>
    [Fact]
    public void TheSchemaArgumentAnswersAndExits()
    {
        (bool handled, int exitCode, string output) = Run(SchemaWriter.Argument);

        handled.Should().BeTrue();
        exitCode.Should().Be(1, "no path is the usage error");
        output.Should().BeEmpty("usage goes to standard error");
    }

    /// <summary>Runs the applets with standard output captured, and restores it whatever happens.</summary>
    private static (bool handled, int exitCode, string output) Run(params string[] args)
    {
        TextWriter original = Console.Out;
        using StringWriter captured = new(CultureInfo.InvariantCulture);
        Console.SetOut(captured);

        try
        {
            bool handled = StartupApplets.TryRun(args, out int exitCode);

            return (handled, exitCode, captured.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
