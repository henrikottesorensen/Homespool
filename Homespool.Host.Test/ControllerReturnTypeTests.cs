using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using AwesomeAssertions;

using Homespool.Host.Controllers;

using Microsoft.AspNetCore.Mvc;

namespace Homespool.Host.Test;

/// <summary>
/// The project rule that a controller action's return type names what it returns:
/// <see cref="ActionResult{TValue}"/> when there is a success body, <see cref="ActionResult"/> when
/// there is not, and <see cref="IActionResult"/> never.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enforced rather than written down, because a convention nobody checks gets missed.</b> The
/// live example is <c>[Collection("WebApplicationFactory")]</c> in the end-to-end suite: fifteen
/// classes carry it, nothing enforces it, the sixteenth omitted it, and the failure surfaced in an
/// unrelated test with no visible connection. The failure available here is quieter still - the
/// OpenAPI document silently loses the response schema, and nothing goes red at all.
/// </para>
/// <para>
/// <b>Razor Pages are deliberately out of scope.</b> <see cref="ActionResult{TValue}"/> exists for
/// content negotiation over a serialised body; a page handler returns <c>Page()</c> or a redirect
/// and has no body to type, so the rule would be 58 signature changes buying nothing but
/// uniformity.
/// </para>
/// </remarks>
public class ControllerReturnTypeTests
{
    private static IEnumerable<Type> Controllers =>
        typeof(PrinterController).Assembly
                                 .GetTypes()
                                 .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract);

    /// <summary>
    /// Every method a controller declares, action or helper - the rule is about the type, and a
    /// private helper returning <see cref="IActionResult"/> puts the interface back into the file it
    /// was removed from.
    /// </summary>
    private static IEnumerable<MethodInfo> DeclaredMethods(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                              | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                  .Where(method => !method.IsSpecialName);

    /// <summary>The type an action actually answers with, unwrapped from its <see cref="Task{T}"/>.</summary>
    private static Type Unwrapped(Type returnType) =>
        returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? returnType.GetGenericArguments()[0]
            : returnType;

    [Fact]
    public void NoControllerMethodReturnsIActionResult()
    {
        // Act
        List<string> offenders = (from controller in Controllers
                                  from method in DeclaredMethods(controller)
                                  where Unwrapped(method.ReturnType) == typeof(IActionResult)
                                  select $"{controller.Name}.{method.Name}").ToList();

        // Assert
        offenders.Should().BeEmpty(
            "an action's return type is meant to say what it returns - use ActionResult<T> where "
            + "there is a body and ActionResult where there is not");
    }

    /// <summary>
    /// The positive half: a public action answers with <c>ActionResult</c> or <c>ActionResult&lt;T&gt;</c>
    /// and nothing else.
    /// </summary>
    /// <remarks>
    /// Note <see cref="ActionResult{TValue}"/> does <b>not</b> derive from <see cref="ActionResult"/> -
    /// it is a separate type implementing <c>IConvertToActionResult</c> - so this cannot be a single
    /// assignability check, and writing it as one would quietly pass everything.
    /// </remarks>
    [Fact]
    public void EveryPublicActionReturnsATypedActionResult()
    {
        // Act
        List<string> offenders = (from controller in Controllers
                                  from method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance
                                                                       | BindingFlags.DeclaredOnly)
                                  where !method.IsSpecialName
                                        && method.GetCustomAttribute<NonActionAttribute>() is null
                                  let returned = Unwrapped(method.ReturnType)
                                  where !typeof(ActionResult).IsAssignableFrom(returned)
                                        && !(returned.IsGenericType
                                             && returned.GetGenericTypeDefinition() == typeof(ActionResult<>))
                                  select $"{controller.Name}.{method.Name} returns {returned.Name}").ToList();

        // Assert
        offenders.Should().BeEmpty();
    }

    /// <summary>
    /// The test above is only worth having if it can fail, and both halves are shaped so that they
    /// would: this pins the discrimination itself rather than the codebase's current state.
    /// </summary>
    [Fact]
    public void TheRuleDistinguishesTheThreeTypesItIsAbout()
    {
        Unwrapped(typeof(Task<IActionResult>)).Should().Be(typeof(IActionResult));

        typeof(ActionResult).IsAssignableFrom(typeof(IActionResult))
                            .Should().BeFalse("the interface must not satisfy the rule");

        typeof(ActionResult).IsAssignableFrom(typeof(ActionResult<string>))
                            .Should().BeFalse("ActionResult<T> is not an ActionResult, which is why "
                                              + "the check above is written as two conditions");

        typeof(ActionResult).IsAssignableFrom(typeof(NoContentResult)).Should().BeTrue();
    }
}
