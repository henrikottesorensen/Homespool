using System;

using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Homespool.Host;

/// <summary>
/// Makes controllers visible to ApiExplorer, and therefore to the OpenAPI document.
/// </summary>
/// <remarks>
/// <para>
/// Without this, the OpenAPI document is empty: ApiExplorer only surfaces controllers that opt in,
/// which normally happens via <c>[ApiController]</c>.
/// </para>
/// <para>
/// <c>[ApiController]</c> is deliberately <b>not</b> used on the PrusaConnect endpoints. It is not a
/// documentation annotation - it also turns on automatic model-validation responses and changes
/// binding-source inference. Those endpoints implement a contract dictated by printer firmware
/// dictated by printer firmware, where the status code is the whole contract and a 400 aborts
/// enrolment once the firmware exhausts its retries. This convention buys visibility and nothing
/// else. New first-party API controllers can use <c>[ApiController]</c> normally.
/// </para>
/// <para>
/// Uses <c>??=</c> so an explicit <c>[ApiExplorerSettings(IgnoreApi = true)]</c> still wins.
/// </para>
/// </remarks>
public class ApiExplorerVisibilityConvention : IControllerModelConvention
{
    public void Apply(ControllerModel controller)
    {
        ArgumentNullException.ThrowIfNull(controller);

        controller.ApiExplorer.IsVisible ??= true;
    }
}
