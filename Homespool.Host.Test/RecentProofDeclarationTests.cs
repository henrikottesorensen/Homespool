using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.RazorPages;

using Homespool.Host.Authentication;

namespace Homespool.Host.Test;

/// <summary>
/// Every page under <c>Pages/Admin</c> and <c>Pages/Account/Manage</c> says what it wants of a recent
/// proof: <see cref="RequireRecentProofAttribute"/> on the class or on at least one handler, or an
/// explicit <see cref="NoRecentProofAttribute"/> with its reason - never silence.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what makes declaring the gate on each page safe.</b> A folder convention would apply
/// it to a new page automatically, and <c>Program.cs</c> declines exactly that for authorisation, so
/// that a reader auditing one page can see what protects it by looking at it. The cost of the
/// per-page declaration is that somebody can forget; this is the test that refuses to let them, and
/// it is <c>AuthorizationDeclarationTests</c>' shape applied to the second decision these pages make.
/// </para>
/// <para>
/// <b>Silence defaults to open</b>, which is the whole reason to insist. A new page in either folder
/// with no declaration is reachable by any session somebody else got hold of, and nothing about it
/// would look wrong in review. Every blind audit so far found one.
/// </para>
/// <para>
/// <b>The exemptions are named</b>, so that adding one is a change to this file and a conversation,
/// and the attribute takes a reason so the conversation is written down.
/// </para>
/// </remarks>
public class RecentProofDeclarationTests
{
    [Fact]
    public void EveryAdministrationPageDeclaresWhatItWantsOfTheProof()
    {
        List<Type> pages = PagesUnder("Homespool.Host.Pages.Admin");

        pages.Should().NotBeEmpty("a reflection test that finds nothing passes for the wrong reason");
        Silent(pages).Should().BeEmpty(
            "a page under /Admin with no declaration is open to any administrator session - " +
            "add [RequireRecentProof], or [NoRecentProof(\"why\")] if it is the exception");
    }

    [Fact]
    public void EveryAccountManagementPageDeclaresWhatItWantsOfTheProof()
    {
        List<Type> pages = PagesUnder("Homespool.Host.Pages.Account.Manage");

        pages.Should().NotBeEmpty();
        Silent(pages).Should().BeEmpty(
            "a page under /Account/Manage with no declaration is open to any session somebody else got hold of - " +
            "add [RequireRecentProof] to the class or the handler that acts, or [NoRecentProof(\"why\")] if it is the exception");
    }

    /// <summary>Nothing under <c>/Admin</c> is exempt. If a page ever is, this fails, which is the conversation worth having.</summary>
    [Fact]
    public void NoAdministrationPageIsExemptFromTheProof()
    {
        Exempt(PagesUnder("Homespool.Host.Pages.Admin")).Should().BeEmpty(
            "the proof is earned under /Account, so no administration page needs to be reachable without one");
    }

    /// <summary>The account pages that are exempt, by name: the ones that read, and the one that takes the password itself.</summary>
    [Fact]
    public void TheExemptAccountManagementPagesAreTheOnesNamedHere()
    {
        Exempt(PagesUnder("Homespool.Host.Pages.Account.Manage")).Should().BeEquivalentTo(
            "IndexModel",
            "ChangePasswordModel",
            "LanguageModel",
            "TwoFactorAuthenticationModel");
    }

    /// <summary>
    /// The one gated act outside the two folders: removing a printer, on a page with two dozen
    /// handlers that want nothing more than a session. Pinned by name because no folder rule reaches
    /// it.
    /// </summary>
    [Fact]
    public void RemovingAPrinterDeclaresTheProof()
    {
        typeof(Homespool.Host.Pages.Printers.DetailModel)
            .GetMethod(nameof(Homespool.Host.Pages.Printers.DetailModel.OnPostRemoveAsync))!
            .GetCustomAttribute<RequireRecentProofAttribute>(inherit: true)
            .Should().NotBeNull("removing a printer destroys what the deployment knows about it");
    }

    /// <summary>
    /// Removing a passkey, on a page whose registration already declares the proof - which satisfies
    /// the folder rule above for the whole page, and is how removal went ungated. Pinned by name.
    /// </summary>
    [Fact]
    public void RemovingAPasskeyDeclaresTheProof()
    {
        typeof(Homespool.Host.Pages.Account.Manage.PasskeysModel)
            .GetMethod(nameof(Homespool.Host.Pages.Account.Manage.PasskeysModel.OnPostRemoveAsync))!
            .GetCustomAttribute<RequireRecentProofAttribute>(inherit: true)
            .Should().NotBeNull("a session somebody else got hold of must not be able to take away the owner's passkeys");
    }

    /// <summary>The proof page itself is exempt, and says why.</summary>
    [Fact]
    public void TheProofPageDeclaresItsOwnExemption()
    {
        typeof(Homespool.Host.Pages.Account.ReauthenticateModel)
            .GetCustomAttribute<NoRecentProofAttribute>(inherit: false)
            .Should().NotBeNull()
            .And.Subject.As<NoRecentProofAttribute>().Reason.Should().NotBeNullOrWhiteSpace();
    }

    private static List<string> Silent(IEnumerable<Type> pages)
    {
        return pages.Where(page => !Declares(page) && page.GetCustomAttribute<NoRecentProofAttribute>(inherit: false) is null)
                    .Select(page => page.FullName!)
                    .ToList();
    }

    private static List<string> Exempt(IEnumerable<Type> pages)
    {
        return pages.Where(page => page.GetCustomAttribute<NoRecentProofAttribute>(inherit: false) is not null)
                    .Select(page => page.Name)
                    .ToList();
    }

    /// <summary>On the class, or on any handler method - the two places the filter reads.</summary>
    private static bool Declares(Type page)
    {
        return page.GetCustomAttribute<RequireRecentProofAttribute>(inherit: true) is not null ||
               page.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                      .Any(method => method.GetCustomAttribute<RequireRecentProofAttribute>(inherit: true) is not null);
    }

    private static List<Type> PagesUnder(string ns)
    {
        return [.. typeof(Homespool.Host.Pages.Admin.SettingsModel).Assembly
                                                                   .GetTypes()
                                                                   .Where(type => type is { IsClass: true, IsAbstract: false } &&
                                                                                  typeof(PageModel).IsAssignableFrom(type) &&
                                                                                  type.Namespace?.StartsWith(ns, StringComparison.Ordinal) == true)];
    }
}
