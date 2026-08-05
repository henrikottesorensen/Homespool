using System;

using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Homespool.Host.Controllers;

/// <summary>
/// Stops MVC reading the request body as a form, so an action can stream a
/// <c>multipart/form-data</c> body itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Without this, streaming a multipart body is impossible, and the failure names nothing.</b>
/// MVC builds its value providers <em>before</em> the action runs, and
/// <see cref="FormValueProviderFactory"/> reacts to the content type alone: it calls
/// <c>ReadFormAsync</c>, which buffers the whole body into <c>Request.Form</c>. The action then gets
/// an exhausted stream and <c>MultipartReader</c> throws <i>"Unexpected end of Stream, the content
/// may have already been read by another component"</i> - a 500 that points at the reader rather than
/// at the thing that drained it.
/// </para>
/// <para>
/// <b>It fires whether or not anything binds from the form.</b> The factories are constructed for the
/// request, not per parameter, so an action taking only a route value is affected exactly as much as
/// one taking an <c>IFormFile</c>. That is what makes it a trap: there is nothing in the action's own
/// signature to suggest a form is being read.
/// </para>
/// <para>
/// Removing the factories rather than clearing the whole list leaves route, query and header binding
/// working - the action still gets its <c>uuid</c> from the route.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    /// <inheritdoc/>
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        for (int i = context.ValueProviderFactories.Count - 1; i >= 0; i--)
        {
            if (context.ValueProviderFactories[i] is FormValueProviderFactory
                or FormFileValueProviderFactory
                or JQueryFormValueProviderFactory)
            {
                context.ValueProviderFactories.RemoveAt(i);
            }
        }
    }

    /// <inheritdoc/>
    public void OnResourceExecuted(ResourceExecutedContext context)
    {
        // Nothing to undo: the factory list is per request.
    }
}
