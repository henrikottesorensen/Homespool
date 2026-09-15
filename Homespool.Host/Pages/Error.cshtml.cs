using System.Diagnostics;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Homespool.Host.Pages;

/// <summary>
/// What a person at a browser sees when a request fails: that it did, and the reference that finds
/// the failure in the server's log.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reached only by the exception handler's re-run</b>, which leaves the response a 500 and this
/// page renders into it. Opened directly there is no failure to describe, so it is a 404.
/// </para>
/// <para>
/// <b>The reference is the trace id, bare</b> - the same 32 hex characters every log line of the
/// request carries as <c>@tr</c>, so a person reading it out is a search of the log. Not
/// <see cref="Activity.Id"/>, which wraps it in a version, a span and flags nothing is logged as.
/// </para>
/// <para>
/// <b>Anonymous and without an antiforgery check</b>, because the failure it describes may be anybody's
/// and the re-run keeps the method: a failed POST arrives here as a POST, carrying a form whose token
/// was either checked already or is what failed.
/// </para>
/// <para>
/// It renders in the ordinary layout. A failure in something the layout itself needs - the database
/// behind the sign-in partial - fails this page too, and the handler then lets the original exception
/// through as the bare 500 there would have been without it.
/// </para>
/// </remarks>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class ErrorModel : PageModel
{
    /// <summary>The request's trace id, as the log carries it.</summary>
    public string TraceId { get; private set; } = string.Empty;

    public IActionResult OnGet()
    {
        return Describe();
    }

    public IActionResult OnPost()
    {
        return Describe();
    }

    private IActionResult Describe()
    {
        if (HttpContext.Features.Get<IExceptionHandlerPathFeature>() is null)
        {
            return NotFound();
        }

        TraceId = Activity.Current?.TraceId.ToHexString() ?? HttpContext.TraceIdentifier;

        return Page();
    }
}
