using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

using Homespool.Host.Exceptions;

namespace Homespool.Host.Authorisation;

/// <summary>
/// Turns <see cref="CredentialScopeDeniedException"/> into <c>403</c> for every controller, rather
/// than letting it reach the error handler as a <c>500</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One filter rather than a <c>catch</c> per action, and the reason is which way the mistake
/// falls.</b> The refusal can come from any endpoint that touches a file, so the alternative is the
/// same three lines at nine call sites and at every one added later. Forgetting one is not a hole -
/// the operation is still refused, and a scope still cannot exceed itself - but it answers <c>500</c>
/// to a caller who did nothing wrong, which reads as "Homespool is broken" rather than "this key may
/// not do that".
/// </para>
/// <para>
/// <b>Deliberately narrow.</b> It maps exactly one exception type and leaves every other refusal
/// where it is: <see cref="TeamAccessDeniedException"/> stays caught per action, because several
/// endpoints turn it into an answer of their own shape - a <c>404</c> that refuses to confirm a UUID,
/// or the print-host shell's <c>text/plain</c>. A filter that swallowed those would flatten
/// distinctions those endpoints make on purpose.
/// </para>
/// <para>
/// <b>Razor Pages are not covered</b>, and do not need to be: a page acts on a sign-in cookie, which
/// is never scoped, so the exception is unreachable there. Should a page ever be reachable by a token,
/// this filter is the wrong mechanism for it and a page handler would want its own message anyway.
/// </para>
/// </remarks>
public sealed class CredentialScopeDeniedFilter : IExceptionFilter
{
    private readonly ILogger<CredentialScopeDeniedFilter> _logger;

    public CredentialScopeDeniedFilter(ILogger<CredentialScopeDeniedFilter> logger)
    {
        _logger = logger;
    }

    public void OnException(ExceptionContext context)
    {
        if (context?.Exception is not CredentialScopeDeniedException denied)
        {
            return;
        }

        // Information rather than Warning: a scoped credential being refused is the feature working,
        // not a fault. It is logged at all because "my script stopped working" is answered by exactly
        // this line.
        _logger.LogInformation("Refused by credential scope: {Reason}", denied.Message);

        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Not permitted by this credential.",
            Detail = denied.Message,
        })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };

        context.ExceptionHandled = true;
    }
}
