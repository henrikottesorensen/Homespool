using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using AwesomeAssertions;

using Homespool.Host.Accounts;
using Homespool.Host.Authentication;

namespace Homespool.Host.Test;

/// <summary>
/// Every enum this repository declares reserves zero for <c>Undefined</c>, and gives every member an
/// explicit value.
/// </summary>
/// <remarks>
/// <para>
/// <b>Zero is <c>Undefined</c> because <c>default(T)</c> is zero and nothing announces itself as
/// defaulted</b>: an unassigned field, a forgotten argument, a result struct nobody filled in, a column
/// added after its rows. Whichever member sits at zero silently becomes the answer, and when that member
/// is a success - a proved step-up, a spent ceremony, an administrator act that went through - a
/// forgotten value reads as the good outcome. <c>Undefined</c> means "nobody wrote this", which is not
/// <c>Unknown</c>, "we looked and could not tell".
/// </para>
/// <para>
/// <b>The analyser does not check this.</b> CA1008 asks for <i>a</i> member at zero and is satisfied
/// by any, including the dangerous ones, which is how six enums reached <c>main</c> with a real answer
/// at zero while the build stayed green.
/// </para>
/// <para>
/// <b>Explicit values because a member's number must not depend on its position.</b> Inserting a
/// member would otherwise renumber everything below it, and whether an enum is stored, logged or sent
/// somewhere as a number is a judgement every new enum would need made correctly.
/// </para>
/// <para>
/// <b>The zero rule is read by reflection and the explicit-value rule from source</b>, since a compiled
/// enum cannot say whether its values were written or implied. The source reader is cross-checked
/// against reflection, so an enum the parser fails to see is a failure rather than a silent pass.
/// </para>
/// </remarks>
public class EnumConventionTests
{
    /// <summary>The projects whose enums the rules cover: everything that ships, and the fake printer the tests drive.</summary>
    private static readonly string[] SourceProjects =
    [
        "Homespool.Host",
        "Homespool.Model",
        "Homespool.Data",
        "Homespool.FakePrinter",
        "Homespool.FakePrinter.Cli",
    ];

    [Fact]
    public void EveryEnumReservesZeroForUndefined()
    {
        // Arrange
        List<Type> enums = LoadedEnums();

        // Act
        List<string> violations = enums.Where(type => ZeroMember(type) != "Undefined")
                                       .Select(type => $"{type.FullName}: zero is {ZeroMember(type) ?? "no member"}")
                                       .ToList();

        // Assert
        enums.Should().NotBeEmpty("a reflection test that finds nothing passes for the wrong reason");
        violations.Should().BeEmpty(
            "default(T) is zero, so the member at zero is what an unset value silently means - "
            + "add 'Undefined = 0' and move the real members up");
    }

    [Fact]
    public void EveryEnumInSourceReservesZeroForUndefined()
    {
        List<string> violations = SourceEnums().Where(declared => declared.ZeroMember != "Undefined")
                                               .Select(declared => $"{declared.File}: {declared.Name} has {declared.ZeroMember ?? "no member"} at zero")
                                               .ToList();

        violations.Should().BeEmpty("the fake printer's enums are not loaded here, so its zero rule is read from source");
    }

    [Fact]
    public void EveryEnumMemberHasAnExplicitValue()
    {
        List<string> violations = SourceEnums().Where(declared => declared.Implicit.Count > 0)
                                               .Select(declared => $"{declared.File}: {declared.Name} leaves {string.Join(", ", declared.Implicit)} to its position")
                                               .ToList();

        violations.Should().BeEmpty("a member whose number is its position is renumbered by the next insertion above it");
    }

    /// <summary>The source reader sees every enum reflection sees in the loaded assemblies, or the two source rules above prove nothing.</summary>
    [Fact]
    public void TheSourceReaderFindsEveryLoadedEnum()
    {
        List<string> loaded = LoadedEnums().Select(type => type.Name).Order(StringComparer.Ordinal).ToList();
        List<string> read = SourceEnums().Where(declared => !declared.File.StartsWith("Homespool.FakePrinter", StringComparison.Ordinal))
                                         .Select(declared => declared.Name)
                                         .Order(StringComparer.Ordinal)
                                         .ToList();

        read.Should().Equal(loaded, "an enum the parser misses would pass both source rules without being read");
    }

    /// <summary>
    /// The three defaults that used to read as success now read as nothing: an unset step-up result is
    /// not a proof, and an unset administration result is not an act that went through.
    /// </summary>
    [Fact]
    public void AnUnsetResultIsNotASuccess()
    {
        default(StepUpResult).Succeeded.Should().BeFalse();
        default(UserAdminResult).Succeeded.Should().BeFalse();
        default(PasskeyCeremonyLedger.SpendResult).Should().NotBe(PasskeyCeremonyLedger.SpendResult.Spent);
    }

    /// <summary>A refusal that names no reason is read as a wrong credential, and cannot be written at all.</summary>
    [Fact]
    public void AnUndefinedSignInRefusalIsNeitherWrittenNorRead()
    {
        Action write = () => SignInRefusals.Fail(SignInRefusal.Undefined, "no reason");

        write.Should().Throw<ArgumentOutOfRangeException>();
        Microsoft.AspNetCore.Authentication.AuthenticateResult.Fail("no reason").Refusal().Should().Be(SignInRefusal.Invalid);
    }

    private static List<Type> LoadedEnums()
    {
        Assembly[] assemblies =
        [
            typeof(UserAdminRefusal).Assembly,
            typeof(Homespool.Model.PrinterStatus).Assembly,
            typeof(Homespool.Data.HomespoolDbContext).Assembly,
        ];

        return [.. assemblies.SelectMany(assembly => assembly.GetTypes())
                             .Where(type => type.IsEnum && type.Namespace?.StartsWith("Homespool", StringComparison.Ordinal) == true)];
    }

    private static string? ZeroMember(Type type)
    {
        return type.GetFields(BindingFlags.Public | BindingFlags.Static)
                   .FirstOrDefault(field => Convert.ToInt64(field.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture) == 0)
                   ?.Name;
    }

    private sealed record DeclaredEnum(string File, string Name, string? ZeroMember, IReadOnlyList<string> Implicit);

    /// <summary>
    /// Every enum declaration in <see cref="SourceProjects"/>, with the member at zero and the members
    /// written without a value.
    /// </summary>
    /// <remarks>
    /// A reader, not a compiler: comments and attributes are removed first, so a <c>=</c> inside a
    /// display attribute or a doc comment is not mistaken for a value. It is kept honest by
    /// <see cref="TheSourceReaderFindsEveryLoadedEnum"/>.
    /// </remarks>
    private static List<DeclaredEnum> SourceEnums()
    {
        string root = SourceRoot();
        Regex declaration = new(@"\benum\s+(\w+)\s*(?::\s*[\w.]+\s*)?\{(.*?)\}", RegexOptions.Singleline);
        List<DeclaredEnum> found = [];

        foreach (string project in SourceProjects)
        {
            foreach (string path in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                string source = StripComments(File.ReadAllText(path));

                foreach (Match match in declaration.Matches(source))
                {
                    string body = Regex.Replace(match.Groups[2].Value, @"\[[^\]]*\]", string.Empty);
                    List<string> members = [.. body.Split(',').Select(member => member.Trim()).Where(member => member.Length > 0)];

                    string? zero = null;
                    List<string> unvalued = [];

                    for (int i = 0; i < members.Count; i++)
                    {
                        string[] parts = members[i].Split('=', 2);

                        if (parts.Length == 2)
                        {
                            if (parts[1].Trim() is "0" or "0x0" or "0x00")
                            {
                                zero = parts[0].Trim();
                            }
                        }
                        else
                        {
                            unvalued.Add(members[i]);

                            if (i == 0)
                            {
                                zero = members[i];
                            }
                        }
                    }

                    found.Add(new DeclaredEnum(Path.GetRelativePath(root, path), match.Groups[1].Value, zero, unvalued));
                }
            }
        }

        found.Should().NotBeEmpty("a source reader that finds nothing passes for the wrong reason");

        return found;
    }

    /// <summary>Removes <c>//</c> and <c>/* */</c> comments, leaving string literals alone.</summary>
    private static string StripComments(string source)
    {
        return Regex.Replace(source,
                             @"""(?:\\.|[^""\\])*""|//[^\n]*|/\*.*?\*/",
                             match => match.Value.StartsWith('"') ? match.Value : string.Empty,
                             RegexOptions.Singleline);
    }

    /// <summary>The repository root, walked up from the test binary.</summary>
    private static string SourceRoot()
    {
        string directory = AppContext.BaseDirectory;

        while (directory is not null && !Directory.Exists(Path.Combine(directory, "Homespool.Host", "Localisation")))
        {
            directory = Path.GetDirectoryName(directory)!;
        }

        directory.Should().NotBeNull("the tests run from inside the repository");

        return directory!;
    }
}
