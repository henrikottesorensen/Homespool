using System;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Homespool.Host.Exceptions;

namespace Homespool.Host.Authorisation;

/// <summary>
/// Answers a <see cref="CredentialScopeDeniedException"/> in plain text, for a controller whose only
/// client puts the body in front of a person.
/// </summary>
/// <remarks>
/// <para>
/// <b>It sits ahead of <see cref="CredentialScopeDeniedFilter"/>, which answers the same refusal with a
/// problem document.</b> An exception filter closer to the action runs first and, once it has handled
/// the exception, the global one never sees it. That document is right for <c>/api/v1</c>, whose
/// callers parse it, and wrong for a slicer, which shows the body verbatim in a dialog with the status
/// in front of it.
/// </para>
/// <para>
/// The text is the exception's own message, which names the capability the credential lacked. An action
/// that has something more useful to say - one that must also say what became of a file - catches the
/// exception itself and never reaches this.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PlainTextScopeRefusalAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context?.Exception is not CredentialScopeDeniedException denied)
        {
            return;
        }

        // Information, as in the global filter: a scoped credential being refused is the feature
        // working, and this line is what answers "my slicer stopped sending".
        context.HttpContext.RequestServices.GetRequiredService<ILogger<PlainTextScopeRefusalAttribute>>()
               .LogInformation("Refused by credential scope: {Reason}", denied.Message);

        context.Result = new ContentResult
        {
            StatusCode = StatusCodes.Status403Forbidden,
            ContentType = "text/plain; charset=utf-8",
            Content = denied.Message,
        };

        context.ExceptionHandled = true;
    }
}
