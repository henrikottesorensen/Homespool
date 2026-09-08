using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.RazorPages;

using Homespool.Host.Authentication;

namespace Homespool.Host.Test;

/// <summary>
/// Every page under <c>Pages/Admin</c> says what it wants of <see cref="AdminElevation"/>:
/// <see cref="RequireAdminElevationAttribute"/> or an explicit
/// <see cref="NoAdminElevationAttribute"/>, never silence.
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
/// <b>What it does not check:</b> that the exemption is <i>right</i>. The challenge page carries one
/// because requiring an elevation to earn an elevation is a redirect loop; a second exemption would
/// pass here and want an argument in review, which is why the attribute takes a reason.
/// </para>
/// </remarks>
public class AdminElevationDeclarationTests
{
    [Fact]
    public void EveryAdministrationPageDeclaresWhatItWantsOfTheElevation()
    {
        // Arrange
        List<Type> pages = AdministrationPages();

        // Act
        List<string> silent = pages
                              .Where(page => page.GetCustomAttribute<RequireAdminElevationAttribute>(inherit: true) is null
                                             && page.GetCustomAttribute<NoAdminElevationAttribute>(inherit: false) is null)
                              .Select(page => page.FullName!)
                              .ToList();

        // Assert
        pages.Should().NotBeEmpty("a reflection test that finds nothing passes for the wrong reason");
        silent.Should().BeEmpty(
            "a page under /Admin with no elevation attribute is open to any administrator session - "
            + "add [RequireAdminElevation], or [NoAdminElevation(\"why\")] if it is the exception");
    }

    /// <summary>
    /// The exemption is one page, named. If a second ever appears this fails, which is the
    /// conversation worth having.
    /// </summary>
    [Fact]
    public void TheOnlyPageExemptFromTheElevationIsTheChallengeItself()
    {
        List<string> exempt = AdministrationPages()
                              .Where(page => page.GetCustomAttribute<NoAdminElevationAttribute>(inherit: false) is not null)
                              .Select(page => page.Name)
                              .ToList();

        exempt.Should().ContainSingle().Which.Should().Be("ChallengeModel");
    }

    private static List<Type> AdministrationPages()
    {
        return [.. typeof(Homespool.Host.Pages.Admin.ChallengeModel).Assembly
                                                                    .GetTypes()
                                                                    .Where(type => type is { IsClass: true, IsAbstract: false }
                                                                                   && typeof(PageModel).IsAssignableFrom(type)
                                                                                   && type.Namespace?.StartsWith("Homespool.Host.Pages.Admin", StringComparison.Ordinal) == true)];
    }
}
