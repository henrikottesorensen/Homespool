using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using Homespool.Host.Controllers;

namespace Homespool.Host.Test;

/// <summary>
/// The project rule that a controller action's return type names every answer it can give: a
/// <see cref="Results{TResult1, TResult2}"/> union of typed results, or one typed result when there
/// is only one answer - never <see cref="IActionResult"/>, <see cref="ActionResult"/>,
/// <see cref="ActionResult{TValue}"/> or a bare <see cref="IResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enforced rather than written down, because a convention nobody checks gets missed.</b> The
/// live example is <c>[Collection("WebApplicationFactory")]</c> in the end-to-end suite: fifteen
/// classes carry it, nothing enforces it, the sixteenth omitted it, and the failure surfaced in an
/// unrelated test with no visible connection. The failure available here is quieter still - the
/// OpenAPI document silently loses a response, and nothing goes red at all.
/// </para>
/// <para>
/// <b>Why unions rather than <c>ActionResult&lt;T&gt;</c> plus attributes.</b> The two halves could
/// disagree, and did: an action declared one body type and documented another, and every test over
/// the attributes passed. A union cannot lie about what it returns, because the compiler will not
/// let an action return an arm it did not declare.
/// </para>
/// <para>
/// <b>Razor Pages are deliberately out of scope.</b> A page handler returns <c>Page()</c> or a
/// redirect and has no serialised body to type, so the rule would be 58 signature changes buying
/// nothing but uniformity.
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
    private static IEnumerable<MethodInfo> DeclaredMethods(Type controller)
    {
        return controller.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                         | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(method => !method.IsSpecialName);
    }

    /// <summary>The type an action actually answers with, unwrapped from its <see cref="Task{T}"/>.</summary>
    private static Type Unwrapped(Type returnType)
    {
        return returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>) ?
            returnType.GetGenericArguments()[0] :
            returnType;
    }

    /// <summary>
    /// The four ways of not saying: the MVC interface, both MVC base shapes, and the minimal-API
    /// interface on its own.
    /// </summary>
    private static bool IsUntyped(Type returned)
    {
        return returned == typeof(IActionResult)
               || returned == typeof(IResult)
               || typeof(ActionResult).IsAssignableFrom(returned)
               || (returned.IsGenericType && returned.GetGenericTypeDefinition() == typeof(ActionResult<>));
    }

    /// <summary>
    /// A <see cref="Results{TResult1, TResult2}"/> of any arity, or a concrete <see cref="IResult"/>
    /// implementation such as <see cref="Ok{TValue}"/> or <see cref="ContentHttpResult"/>.
    /// </summary>
    private static bool IsTyped(Type returned)
    {
        if (returned.IsGenericType && returned.GetGenericTypeDefinition().Name.StartsWith("Results`", StringComparison.Ordinal)
                                   && returned.Namespace == typeof(Results<,>).Namespace)
        {
            return true;
        }

        return typeof(IResult).IsAssignableFrom(returned) && !returned.IsInterface;
    }

    [Fact]
    public void NoControllerMethodReturnsAnUntypedResult()
    {
        // Act
        List<string> offenders = (from controller in Controllers
            from method in DeclaredMethods(controller)
            let returned = Unwrapped(method.ReturnType)
            where IsUntyped(returned)
            select $"{controller.Name}.{method.Name} returns {returned.Name}").ToList();

        // Assert
        offenders.Should().BeEmpty(
            "an action's return type is meant to say what it returns - a Results<> union of typed "
            + "results, or a single typed result");
    }

    /// <summary>
    /// The positive half: a public action answers with a <c>Results&lt;...&gt;</c> union or one
    /// concrete result type, and nothing else.
    /// </summary>
    [Fact]
    public void EveryPublicActionReturnsATypedResult()
    {
        // Act
        List<string> offenders = (from controller in Controllers
            from method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance
                                                                     | BindingFlags.DeclaredOnly)
            where !method.IsSpecialName
                  && method.GetCustomAttribute<NonActionAttribute>() is null
            let returned = Unwrapped(method.ReturnType)
            where !IsTyped(returned)
            select $"{controller.Name}.{method.Name} returns {returned.Name}").ToList();

        // Assert
        offenders.Should().BeEmpty();
    }

    /// <summary>
    /// The tests above are only worth having if they can fail, and both halves are shaped so that
    /// they would: this pins the discrimination itself rather than the codebase's current state.
    /// </summary>
    [Fact]
    public void TheRuleDistinguishesTheTypesItIsAbout()
    {
        Unwrapped(typeof(Task<IActionResult>)).Should().Be(typeof(IActionResult));

        IsUntyped(typeof(IActionResult)).Should().BeTrue();
        IsUntyped(typeof(IResult)).Should().BeTrue("the bare interface says nothing about the answer");
        IsUntyped(typeof(ActionResult)).Should().BeTrue();
        IsUntyped(typeof(ActionResult<string>)).Should().BeTrue("ActionResult<T> is not an ActionResult, "
                                                                + "which is why the check names it separately");
        IsUntyped(typeof(NoContentResult)).Should().BeTrue("an MVC result is the old shape, however specific");

        IsTyped(typeof(Results<Ok<string>, NotFoundProblem>)).Should().BeTrue();
        IsTyped(typeof(Results<Ok, NoContent, ForbiddenProblem, NotFoundProblem, ConflictProblem, BadGatewayProblem>))
            .Should().BeTrue("the rule holds at every arity");
        IsTyped(typeof(Ok<string>)).Should().BeTrue("one answer needs no union");
        IsTyped(typeof(ContentHttpResult)).Should().BeTrue();
        IsTyped(typeof(IResult)).Should().BeFalse();
        IsTyped(typeof(ActionResult<string>)).Should().BeFalse();
    }
}
