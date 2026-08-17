using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

using Homespool.Host.Localisation;

namespace Homespool.Host.Test;

/// <summary>
/// A localiser reading the shipped resources, for page models constructed by hand.
/// </summary>
/// <remarks>
/// <b>Real rather than substituted.</b> A page model built with a stub would render whatever the
/// stub returns, so a test asserting on a status message would pass with the resource missing,
/// misspelt or absent from the assembly - which is the failure worth catching. It costs one service
/// provider per call and these are not hot paths.
/// </remarks>
internal static class TestLocaliser
{
    /// <summary>The shared-resource localiser, resolved the way the application resolves one.</summary>
    public static IStringLocalizer<SharedResource> Shared()
    {
        return new ServiceCollection()
               .AddLogging()
               .AddLocalization()
               .BuildServiceProvider()
               .GetRequiredService<IStringLocalizer<SharedResource>>();
    }

    /// <summary>
    /// The error-to-sentence translator, reading the shipped resources.
    /// </summary>
    /// <remarks>
    /// Real for the same reason as <see cref="Shared"/>, and more so: what this renders used to be
    /// <c>exception.Message</c>, so a stub would let a page keep showing an English sentence while
    /// the test agreed it was localised.
    /// </remarks>
    public static ErrorText Errors()
    {
        IStringLocalizer<SharedResource> localiser = Shared();

        return new ErrorText(localiser, new PrinterStatusText(localiser));
    }
}
