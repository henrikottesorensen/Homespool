using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Homespool.Host.Test;

/// <summary>
/// Every Razor page model and every controller says whether it needs a signed-in account:
/// <see cref="AuthorizeAttribute"/> or <see cref="AllowAnonymousAttribute"/>, never silence.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule already existed and nothing enforced it.</b> <c>Program.cs</c> deliberately declines
/// an <c>AuthorizeFolder</c> convention for <c>Account/Manage</c> so that "a reader auditing one page
/// can see whether it is protected by looking at it" — which is only true if every page says. Until
/// this test, a page that said nothing was indistinguishable from one that had decided to be public,
/// and the default for saying nothing is public.
/// </para>
/// <para>
/// <b>It exists because a page was written without one.</b> <c>Account/Manage/ExternalLogins</c> was
/// added with no attribute; the omission was visible only as an "unnecessary using directive" warning
/// on <c>Microsoft.AspNetCore.Authorization</c>, which is the analyser reporting the symptom of a
/// missing attribute as a tidiness problem. Nothing else would have caught it —
/// <c>ManageFolderAuthorizationTests</c> covers that folder thoroughly but lists its pages by hand in
/// <c>InlineData</c>, so a page nobody adds to the list is a page it does not test.
/// </para>
/// <para>
/// <b>What this does not check, stated so it is not mistaken for more than it is:</b> that the
/// attribute is the <i>right</i> one. A page wrongly marked <see cref="AllowAnonymousAttribute"/>
/// passes here exactly as a correct one does. The failure this removes is the silent default, not a
/// wrong decision — for the second, <c>ManageFolderAuthorizationTests</c> drives real requests.
/// </para>
/// <para>
/// <b>Inherited attributes count</b>, since a base class carrying the decision is still the decision
/// being made in a place a reader can find. Nothing in this codebase does that today.
/// </para>
/// <para>
/// <b>Controllers were already compliant when this was written</b> — all eight declared, so extending
/// it to them cost nothing and is worth more than it cost: an undeclared action under <c>/api/v1</c>
/// is a worse thing to acquire by accident than an undeclared page. It does not check that the
/// attribute names the right <i>policy</i>, which for the API is the distinction that matters: a bare
/// <see cref="AuthorizeAttribute"/> there authenticates by cookie only and silently refuses personal
/// access tokens, where <c>Authorisation.Policies.Api</c> accepts both.
/// </para>
/// <para>
/// <b>What it cannot see: endpoints that are not classes.</b> Anything mapped directly in
/// <c>Program.cs</c> — <c>MapGet</c>, <c>MapHealthChecks</c> and the like — has no type to carry an
/// attribute and is not covered here. The rule this enforces is about the two families that do.
/// </para>
/// </remarks>
public class AuthorizationDeclarationTests
{
    /// <summary>
    /// Every concrete page model and controller in the application assembly. Located by reflection
    /// rather than by a list, which is the whole point: a list is a thing a new one can be missing
    /// from — and the hand-written <c>InlineData</c> list in <c>ManageFolderAuthorizationTests</c> is
    /// the worked example of that going wrong.
    /// </summary>
    public static TheoryData<Type> EndpointTypes()
    {
        TheoryData<Type> data = [];

        foreach (Type type in typeof(Program).Assembly
                                             .GetTypes()
                                             .Where(t => (typeof(PageModel).IsAssignableFrom(t)
                                                          || typeof(ControllerBase).IsAssignableFrom(t))
                                                         && t is { IsAbstract: false, IsPublic: true })
                                             .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            data.Add(type);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EndpointTypes))]
    public void EveryEndpointDeclaresWhetherItNeedsAnAccount(Type endpoint)
    {
        bool declares = endpoint.GetCustomAttribute<AuthorizeAttribute>(inherit: true) is not null
                        || endpoint.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) is not null;

        declares.Should().BeTrue(
            "{0} carries neither [Authorize] nor [AllowAnonymous], so whether it is reachable without "
            + "an account is decided by the framework default rather than by anybody. Public is the "
            + "default; say so explicitly if that is what it should be.",
            endpoint.FullName);
    }

    /// <summary>
    /// That the reflection above actually found them. A query that silently matched nothing would
    /// make every assertion above vacuously true, and this suite would go green the day the namespace
    /// moved.
    /// </summary>
    [Fact]
    public void TheEndpointsAreActuallyBeingFound()
    {
        IReadOnlyList<Type> found = [.. EndpointTypes()];

        found.Should().HaveCountGreaterThan(30, "the application has dozens of these, so a handful means the query is wrong");
        found.Should().Contain(t => t.Name == "ExternalLoginsModel", "the page that prompted this test is a page");
        found.Should().Contain(t => typeof(ControllerBase).IsAssignableFrom(t), "controllers are in scope too");
    }
}
