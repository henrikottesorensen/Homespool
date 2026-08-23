using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AwesomeAssertions;

namespace Homespool.Host.Test;

/// <summary>
/// Nothing in the application takes a password away from an account, which is what keeps a deployment
/// recoverable when its identity provider dies.
/// </summary>
/// <remarks>
/// <para>
/// <b>The guarantee this protects.</b> <c>Setup</c> creates the first administrator with
/// <c>CreateAsync(user, Input.Password)</c>, an account is never hard deleted, and nothing removes a
/// password — so there is always at least one administrator who can sign in locally. That matters
/// because an account created through an identity provider has no password by rule and cannot obtain
/// one on its own: its recovery is an administrator sending it an invite, and the administrator has to
/// be able to get in to send it.
/// </para>
/// <para>
/// <b>It holds today by accident, not by design, which is why it is pinned here.</b> Every step of it
/// is a separate decision made for its own reasons, and none of them mentions the others.
/// <c>AdminBootstrap</c> disables first-time setup on <c>admins.Count &gt; 0</c> — an administrator
/// who exists but cannot sign in still counts — so the day something does remove a password, a
/// deployment can reach a state with no administrator able to sign in and no bootstrap door open,
/// recoverable only by editing the database. Nothing would go red.
/// </para>
/// <para>
/// <b>The plausible way in is a reasonable-looking feature</b>, not a mistake: an administrator screen
/// offering to revoke somebody's local access, or a "this account is provider-only now" tidy-up. Both
/// would be one <c>RemovePasswordAsync</c> call, and both would look correct in review. This test is
/// the objection they need to answer.
/// </para>
/// <para>
/// <b>It reads the source rather than the IL</b>, which is the honest limit: it would not see the call
/// made through reflection, and it would flag the identifier appearing in a comment. That is worth it
/// for a check with no dependencies — and the failure mode is a false alarm somebody investigates, not
/// a silent pass. If this ever needs to be exact, the answer is IL inspection, not a cleverer pattern.
/// </para>
/// </remarks>
public class LastLocalCredentialTests
{
    /// <summary>
    /// The call that would break the guarantee. Removing a password is the only operation that takes
    /// an account from "can sign in locally" back to "cannot", and Identity offers exactly one.
    /// </summary>
    private const string ForbiddenCall = "RemovePasswordAsync";

    /// <summary>
    /// Projects whose code runs in a deployment. The test suites use the call deliberately, to build
    /// the provider-only state they exercise, so they are not scanned.
    /// </summary>
    private static readonly string[] ProductionProjects = ["Homespool.Host", "Homespool.Data", "Homespool.Model"];

    [Fact]
    public void NothingInProductionCodeRemovesAPassword()
    {
        IReadOnlyList<string> offenders =
        [
            .. ProductionSourceFiles()
               .Where(f => File.ReadAllText(f).Contains(ForbiddenCall, StringComparison.Ordinal))
               .Select(Relative)
               .Order(StringComparer.Ordinal)
        ];

        offenders.Should().BeEmpty(
            "an account that loses its password cannot get another one - ForgotPassword is gated and "
            + "ChangePassword refuses - so this is what keeps at least one administrator able to sign "
            + "in and issue the invites that recover everybody else. If the call is genuinely wanted, "
            + "the thing to settle first is how a deployment gets back in once no administrator can, "
            + "because AdminBootstrap will not reopen setup for an administrator that merely exists");
    }

    /// <summary>
    /// That the scan actually read the application. A walk that silently matched nothing would make
    /// the assertion above vacuously true, and this file would go green the day a directory moved.
    /// </summary>
    [Fact]
    public void TheProductionSourceIsActuallyBeingRead()
    {
        IReadOnlyList<string> files = [.. ProductionSourceFiles()];

        files.Should().HaveCountGreaterThan(100, "the application is hundreds of files, so a handful means the walk is wrong");

        files.Select(Relative).Should()
             .Contain(Path.Combine("Homespool.Host", "Program.cs"), "the entry point is production source");

        // The scan can see the call when it is there: the suites use it, and they are the reason
        // ProductionProjects excludes them rather than the whole tree being searched.
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot().FullName, "Homespool.Host.E2ETest"), "*.cs")
                 .Should().Contain(f => File.ReadAllText(f).Contains(ForbiddenCall, StringComparison.Ordinal),
                                   "if no test used it either, this whole check would prove nothing");
    }

    private static IEnumerable<string> ProductionSourceFiles()
    {
        DirectoryInfo root = RepositoryRoot();

        foreach (string project in ProductionProjects)
        {
            string path = Path.Combine(root.FullName, project);

            if (!Directory.Exists(path))
            {
                throw new InvalidOperationException($"No {project} directory under {root.FullName}.");
            }

            foreach (string file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(path, file);

                // Build output, and the migration designer files nobody writes by hand.
                if (relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

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

    private static string Relative(string path)
    {
        return Path.GetRelativePath(RepositoryRoot().FullName, path);
    }
}
