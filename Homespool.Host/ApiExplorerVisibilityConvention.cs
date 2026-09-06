using System;

using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Homespool.Host;

/// <summary>
/// Makes controllers visible to ApiExplorer, and therefore to the OpenAPI document.
/// </summary>
/// <remarks>
/// <para>
/// ApiExplorer only surfaces controllers that opt in, which normally happens via
/// <c>[ApiController]</c>. Applying it here makes visibility a property of being a controller in this
/// application, so the document does not quietly lose an endpoint whose attributes change.
/// </para>
/// <para>
/// Visibility is all it buys. <c>[ApiController]</c> is a behaviour switch as much as a documentation
/// one - automatic model-validation responses and binding-source inference come with it - and every
/// controller here carries it, the printer-facing ones included, so those behaviours are in force on
/// the firmware-dictated routes too. <c>PrusaConnectPrinterController</c> says what that means where
/// it lands.
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
