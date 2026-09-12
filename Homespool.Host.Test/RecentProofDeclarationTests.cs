using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.RazorPages;

using Homespool.Host.Authentication;

namespace Homespool.Host.Test;

/// <summary>
/// Every page under <c>Pages/Admin</c> says what it wants of a recent proof:
/// <see cref="RequireRecentProofAttribute"/> or an explicit
/// <see cref="NoRecentProofAttribute"/>, never silence.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what makes declaring the gate on each page safe.</b> A folder convention would apply
/// it to a new page automatically, and <c>Program.cs</c> declines exactly that for authorisation, so
/// that a reader auditing one page can see what protects it by looking at it. The cost of the
/// per-page declaration is that somebody can forget; this is the test that refuses to let them, and
/// it is <c>AuthorizationDeclarationTests</c>' shape applied to the second decision an
/// administration page makes.
/// </para>
/// <para>
/// <b>Silence defaults to open</b>, which is the whole reason to insist. A new page under
/// <c>/Admin</c> with no attribute is reachable by any administrator whose session somebody else got
/// hold of, and nothing about it would look wrong in review.
/// </para>
/// <para>
/// <b>What it does not check:</b> that an exemption is <i>right</i>. None exists under <c>/Admin</c>
/// today - the proof page lives under <c>/Account</c> - and the first one to appear fails the second
/// test below, which is the conversation worth having; the attribute takes a reason for it.
/// </para>
/// </remarks>
public class RecentProofDeclarationTests
{
    [Fact]
    public void EveryAdministrationPageDeclaresWhatItWantsOfTheProof()
    {
        // Arrange
        List<Type> pages = AdministrationPages();

        // Act
        List<string> silent = pages
                              .Where(page => page.GetCustomAttribute<RequireRecentProofAttribute>(inherit: true) is null
                                             && page.GetCustomAttribute<NoRecentProofAttribute>(inherit: false) is null)
                              .Select(page => page.FullName!)
                              .ToList();

        // Assert
        pages.Should().NotBeEmpty("a reflection test that finds nothing passes for the wrong reason");
        silent.Should().BeEmpty(
            "a page under /Admin with no declaration is open to any administrator session - "
            + "add [RequireRecentProof], or [NoRecentProof(\"why\")] if it is the exception");
    }

    /// <summary>
    /// Nothing under <c>/Admin</c> is exempt. If a page ever is, this fails, which is the
    /// conversation worth having.
    /// </summary>
    [Fact]
    public void NoAdministrationPageIsExemptFromTheProof()
    {
        List<string> exempt = AdministrationPages()
                              .Where(page => page.GetCustomAttribute<NoRecentProofAttribute>(inherit: false) is not null)
                              .Select(page => page.Name)
                              .ToList();

        exempt.Should().BeEmpty("the proof is earned under /Account, so no administration page needs to be reachable without one");
    }

    /// <summary>The proof page itself is the one exemption anywhere, and says why.</summary>
    [Fact]
    public void TheProofPageDeclaresItsOwnExemption()
    {
        typeof(Homespool.Host.Pages.Account.ReauthenticateModel)
            .GetCustomAttribute<NoRecentProofAttribute>(inherit: false)
            .Should().NotBeNull()
            .And.Subject.As<NoRecentProofAttribute>().Reason.Should().NotBeNullOrWhiteSpace();
    }

    private static List<Type> AdministrationPages()
    {
        return [.. typeof(Homespool.Host.Pages.Admin.SettingsModel).Assembly
                                                                   .GetTypes()
                                                                   .Where(type => type is { IsClass: true, IsAbstract: false }
                                                                                  && typeof(PageModel).IsAssignableFrom(type)
                                                                                  && type.Namespace?.StartsWith("Homespool.Host.Pages.Admin", StringComparison.Ordinal) == true)];
    }
}
