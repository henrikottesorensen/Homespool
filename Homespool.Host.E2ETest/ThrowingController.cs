using System;

using Microsoft.AspNetCore.Mvc;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Endpoints that fail on purpose, for <see cref="ErrorPageTests"/>. Nothing in the application
/// references this assembly, so they exist only in a host that adds it as an application part.
/// </summary>
/// <remarks>
/// A controller rather than a substituted service on a real page: the failure is the thing under
/// test, and a page's own handling of a dependency - catching it, or failing before the handler that
/// was meant to throw - would decide what the test saw.
/// </remarks>
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class ThrowingController : ControllerBase
{
    /// <summary>The exception's message, which only the developer exception page may show.</summary>
    public const string Message = "A failure the error page tests asked for.";

    [HttpGet("/test-only/throws")]
    [HttpPost("/test-only/throws")]
    [HttpGet("/api/test-only/throws")]
    [HttpGet("/compat/test-only/throws")]
    public IActionResult Throw()
    {
        throw new InvalidOperationException(Message);
    }
}
