using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AwesomeAssertions;

namespace Homespool.Host.Test;

/// <summary>
/// The passkey scheme stands on its own: nothing in the application asks <c>SignInManager</c> to run a
/// passkey ceremony, so a passkey is never an input to Identity's two-factor state machine.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the framework offers, and why it is refused.</b> <c>SignInManager</c> has five passkey
/// methods. They hold the ceremony's state in the two-factor cookie between the challenge and the
/// assertion, and the one that completes a sign-in runs the first-factor-then-second-factor flow,
/// which makes a passkey a first factor that still owes an authenticator code. The
/// <c>Passkey</c> scheme drives the engine underneath those methods directly and keeps its own state,
/// so a passkey sign-in is complete in itself. The five are the only way back into the coupling, and
/// each would look like a reasonable shortcut in review.
/// </para>
/// <para>
/// <b>It reads the source rather than the IL</b>, as <see cref="LastLocalCredentialTests"/> does and
/// for the same reason: no dependencies, and a false alarm somebody reads beats a silent pass. A
/// comment naming one of these would trip it, which is why this file's own remarks do not.
/// </para>
/// </remarks>
public class PasskeyIndependenceTests
{
    /// <summary>
    /// <c>SignInManager</c>'s passkey surface, each spelled as the identifier a call site would use.
    /// </summary>
    private static readonly string[] ForbiddenCalls =
    [
        "Make" + "PasskeyCreationOptionsAsync",
        "Make" + "PasskeyRequestOptionsAsync",
        "Perform" + "PasskeyAttestationAsync",
        "Perform" + "PasskeyAssertionAsync",
        "Passkey" + "SignInAsync",
    ];

    private static readonly string[] ProductionProjects = ["Homespool.Host", "Homespool.Data", "Homespool.Model"];

    [Fact]
    public void NothingInProductionCodeRunsAPasskeyCeremonyThroughSignInManager()
    {
        IReadOnlyList<string> offenders =
        [
            .. ProductionSourceFiles()
               .Select(path => (path, text: File.ReadAllText(path)))
               .SelectMany(file => ForbiddenCalls
                                   .Where(call => file.text.Contains(call, StringComparison.Ordinal))
                                   .Select(call => $"{Relative(file.path)}: {call}"))
               .Order(StringComparer.Ordinal)
        ];

        offenders.Should().BeEmpty(
            "the Passkey scheme holds a ceremony's state itself and completes a sign-in on its own; "
            + "SignInManager's passkey methods put that state in the two-factor cookie and route the "
            + "sign-in through the first-then-second-factor flow, which is the coupling this scheme "
            + "exists to avoid. Drive IPasskeyHandler through PasskeyAuthenticationHandler instead");
    }

    /// <summary>
    /// That the scan read the application at all: a walk matching nothing would make the assertion
    /// above vacuously true.
    /// </summary>
    [Fact]
    public void TheScanReadsTheHandlerThatDrivesTheEngine()
    {
        ProductionSourceFiles()
            .Select(Relative)
            .Should().Contain(path => path.EndsWith("PasskeyAuthenticationHandler.cs", StringComparison.Ordinal));
    }

    private static IEnumerable<string> ProductionSourceFiles()
    {
        string root = SourceRoot();

        return ProductionProjects
               .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
               .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                              && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string Relative(string path)
    {
        return Path.GetRelativePath(SourceRoot(), path);
    }

    private static string SourceRoot()
    {
        string? directory = AppContext.BaseDirectory;

        while (directory is not null && !File.Exists(Path.Combine(directory, "Homespool.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        directory.Should().NotBeNull("the tests run from inside the repository");

        return directory!;
    }
}
