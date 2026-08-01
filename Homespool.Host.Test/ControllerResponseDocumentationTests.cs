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
/// That every action says which status codes it can answer with, and that an action promising a body
/// documents that body's type - the half of the OpenAPI document <c>ActionResult&lt;T&gt;</c> alone
/// cannot supply.
/// </summary>
/// <remarks>
/// <para>
/// Companion to <see cref="ControllerReturnTypeTests"/>, and enforced for the same reason: the cost
/// of forgetting is a quietly thinner document, which nothing else would notice.
/// </para>
/// <para>
/// <b>Scoped to the app API.</b> <see cref="PrusaConnectPrinterController"/> is excluded - <c>/p/*</c>
/// is Prusa's protocol rather than ours, its only clients are printers running firmware that was
/// written against Connect, and nobody will ever read our OpenAPI document to implement against it.
/// </para>
/// </remarks>
public class ControllerResponseDocumentationTests
{
    private static IEnumerable<Type> AppApiControllers =>
    [
        typeof(PrinterController),
        typeof(PrintFileController),
        typeof(PrinterAppController),
    ];

    private static IEnumerable<MethodInfo> Actions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                  .Where(method => !method.IsSpecialName
                                   && method.GetCustomAttribute<NonActionAttribute>() is null);

    private static Type Unwrapped(Type returnType) =>
        returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? returnType.GetGenericArguments()[0]
            : returnType;

    [Fact]
    public void EveryAppApiActionDocumentsItsStatusCodes()
    {
        // Act
        List<string> undocumented = (from controller in AppApiControllers
                                     from action in Actions(controller)
                                     where !action.GetCustomAttributes<ProducesResponseTypeAttribute>().Any()
                                     select $"{controller.Name}.{action.Name}").ToList();

        // Assert
        undocumented.Should().BeEmpty("an endpoint's status codes are part of its contract");
    }

    /// <summary>
    /// An action declaring <c>ActionResult&lt;T&gt;</c> must document a success response carrying
    /// that same <c>T</c> - otherwise the two halves disagree and the generated schema is the one
    /// that is wrong.
    /// </summary>
    [Fact]
    public void AnActionPromisingABodyDocumentsThatBodysType()
    {
        // Act
        List<string> mismatched = [];

        foreach (Type controller in AppApiControllers)
        {
            foreach (MethodInfo action in Actions(controller))
            {
                Type returned = Unwrapped(action.ReturnType);

                if (!returned.IsGenericType || returned.GetGenericTypeDefinition() != typeof(ActionResult<>))
                {
                    continue;
                }

                Type body = returned.GetGenericArguments()[0];

                bool documented = action.GetCustomAttributes<ProducesResponseTypeAttribute>()
                                        .Any(attribute => attribute.Type == body
                                                          && attribute.StatusCode is >= 200 and < 300);

                if (!documented)
                {
                    mismatched.Add($"{controller.Name}.{action.Name} returns ActionResult<{body.Name}> "
                                   + "with no matching 2xx ProducesResponseType");
                }
            }
        }

        // Assert
        mismatched.Should().BeEmpty();
    }
}
